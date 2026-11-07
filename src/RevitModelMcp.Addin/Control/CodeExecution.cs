using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Control;

#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif

namespace RevitModelMcp.Control;

public sealed class ScriptContext
{
    private readonly List<string> _log = [];
    [ThreadStatic] internal static ScriptContext? Current;
    private readonly List<IDisposable> _scopes = [];

    public static T TrackScope<T>(T scope) where T : IDisposable
    {
        if (scope is not null) Current?._scopes.Add(scope);
        return scope!;
    }

    internal bool CloseOpenScopes()
    {
        var leftOpen = false;
        foreach (var scope in _scopes.AsEnumerable().Reverse())
        {
            switch (scope)
            {
                case SubTransaction { IsValidObject: true } subTransaction when subTransaction.GetStatus() == TransactionStatus.Started:
                    leftOpen = true;
                    subTransaction.RollBack();
                    break;
                case Transaction { IsValidObject: true } transaction when transaction.GetStatus() == TransactionStatus.Started:
                    leftOpen = true;
                    transaction.RollBack();
                    break;
                case TransactionGroup { IsValidObject: true } group when group.GetStatus() == TransactionStatus.Started:
                    leftOpen = true;
                    group.RollBack();
                    break;
                case EditScope { IsValidObject: true, IsActive: true } editScope:
                    leftOpen = true;
                    editScope.Cancel();
                    break;
            }
        }
        return leftOpen;
    }

    internal ScriptContext(UIApplication uiApplication, UIDocument? uiDocument, Document? document)
    {
        UiApplication = uiApplication;
        UiDocument = uiDocument;
        Document = document;
    }

    public UIApplication UiApplication { get; }
    public Autodesk.Revit.ApplicationServices.Application Application => UiApplication.Application;
    public UIDocument? UiDocument { get; }
    public Document? Document { get; }
    public IReadOnlyList<string> Lines => _log;
    public IReadOnlyDictionary<string, object> Args { get; internal set; } = new Dictionary<string, object>();
    public void Log(string message)
    {
        foreach (var line in (message ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (_log.Count == 2000) break;
            _log.Add(line);
        }
    }
    public double ToMm(double feet) => feet * 304.8;
    public double FromMm(double millimeters) => millimeters / 304.8;
}

internal static class CodeExecution
{
    private sealed class CompiledCode(byte[] assembly, byte[] symbols)
    {
        public byte[] Assembly { get; } = assembly;
        public byte[] Symbols { get; } = symbols;
        public System.Reflection.Assembly? Loaded { get; set; }
    }

    private static readonly BoundedLruCache<string, CompiledCode> Cache = new(32);

    public static ActionResultData Execute(UIApplication application, Document? document, UIDocument? uiDocument,
        ActionJobContract action, ActionCommandExecutor.ActionFailures failures, string clientName, string? jobId, bool compileOnly = false)
    {
        var stopwatch = Stopwatch.StartNew();
        var code = action.Code ?? throw new ArgumentException("code is required.");
        var documentTitle = document?.Title ?? string.Empty;
        var result = new ActionResultData
        {
            Title = document?.Title,
            Log = [],
            DryRun = action.DryRun,
            Summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
            {
                Command = "execute-code",
                DocumentTitle = documentTitle,
                DryRun = action.DryRun
            })
        };
        if (action.TransactionMode == "none")
            result.Warning = "The script owns transactions and document lifecycle. Undo is not guaranteed.";
        if (!compileOnly) WriteAudit(code, clientName, document?.Title, document?.PathName, action.TransactionMode, jobId);
        if (!compileOnly && action.ToolName is not null)
            PluginLog.Info(ToolSession.AuditText(action.ToolName, action.ToolRevision!, action.Args.Keys));
        var key = action.ToolRevision is null ? CodeSource.CacheKey(code, action.TransactionMode) : ToolSession.CacheKey(action.ToolRevision) + ":" + action.TransactionMode;
        if (!Cache.TryGet(key, out var compiled) || compileOnly)
        {
            var source = CodeSource.BuildSource(code);
            var references = GetReferences();
            var compilation = CSharpCompilation.Create("McpScript_" + key.Replace(":", "_").Substring(0, 16),
                [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release));
            if (action.TransactionMode == "group")
            {
                var tree = compilation.SyntaxTrees.Single();
                var rewritten = new ScopeTrackingRewriter(compilation.GetSemanticModel(tree)).Visit(tree.GetRoot())!;
                compilation = compilation.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(rewritten, tree.Options));
            }
            using var assemblyStream = new MemoryStream();
            using var symbolsStream = new MemoryStream();
            var emitted = compilation.Emit(assemblyStream, symbolsStream,
                options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            if (!emitted.Success)
            {
                result.CodeError = "compilation failed";
                result.Summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
                {
                    Command = "execute-code",
                    DocumentTitle = documentTitle,
                    CodeFailure = CodeFailureKind.Compilation
                });
                result.Diagnostics = emitted.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)
                    .Select(item =>
                    {
                        var position = item.Location.GetMappedLineSpan().StartLinePosition;
                        return new CodeDiagnostic
                        {
                            Line = Math.Max(1, position.Line + 1),
                            Column = Math.Max(1, position.Character + 1),
                            Id = item.Id,
                            Message = item.GetMessage()
                        };
                    }).ToList();
                result.ElapsedMs = stopwatch.ElapsedMilliseconds;
                return result;
            }
            if (compileOnly)
            {
                var scripts = compilation.GetSymbolsWithName("Script", SymbolFilter.Type).OfType<INamedTypeSymbol>()
                    .Where(type => type.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public).ToList();
                var methods = scripts.SelectMany(type => type.GetMembers("Execute").OfType<IMethodSymbol>()).ToList();
                if (scripts.Count != 1 || methods.Count != 1 || methods[0].DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public ||
                    !methods[0].IsStatic || methods[0].ReturnType.SpecialType != Microsoft.CodeAnalysis.SpecialType.System_Object ||
                    methods[0].Parameters.Length != 1 || methods[0].Parameters[0].Type.ToDisplayString() != typeof(ScriptContext).FullName)
                {
                    result.CodeError = "Script must declare public static object Execute(ScriptContext ctx).";
                    result.Summary = "Tool was not saved: invalid Script.Execute signature.";
                    return result;
                }
            }
            compiled = new CompiledCode(assemblyStream.ToArray(), symbolsStream.ToArray());
            Cache.Set(key, compiled);
        }

        if (compileOnly) return result;
        var context = new ScriptContext(application, uiDocument, document) { Args = action.Args };
        TransactionGroup? group = null;
        Transaction? transaction = null;
        var changedElementCount = 0;
        void CountChanges(object? sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs arguments)
        {
            if (arguments.Operation != Autodesk.Revit.DB.Events.UndoOperation.TransactionCommitted) return;
            if (document is null || !document.Equals(arguments.GetDocument())) return;
            changedElementCount += arguments.GetAddedElementIds().Count + arguments.GetModifiedElementIds().Count
                + arguments.GetDeletedElementIds().Count;
        }
        var counting = action.TransactionMode is "auto" or "group";
        if (counting) application.Application.DocumentChanged += CountChanges;
#if !NETFRAMEWORK
        var loadContext = new AssemblyLoadContext("McpScript_" + key.Substring(0, 12), isCollectible: true);
        loadContext.Resolving += (_, name) => GetBoundAssemblies().Concat(AppDomain.CurrentDomain.GetAssemblies())
            .FirstOrDefault(item => item.GetName().Name == name.Name);
#endif
        try
        {
            if (action.TransactionMode is "auto" or "group")
            {
                if (document is null) throw new InvalidOperationException("No active Revit document.");
                group = new TransactionGroup(document, "MCP action");
                if (group.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Could not start the action transaction group.");
            }
            if (action.TransactionMode == "auto")
            {
                transaction = new Transaction(document!, "revit_execute_code");
                if (transaction.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Could not start the action transaction.");
                transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
                    .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
            }
#if NETFRAMEWORK
            // .NET Framework cannot unload assemblies, so load once per cache entry.
            // Reuse bounds growth to the cache size per distinct source.
            var script = compiled.Loaded ??= Assembly.Load(compiled.Assembly, compiled.Symbols);
#else
            using var assemblyInput = new MemoryStream(compiled.Assembly);
            using var symbolsInput = new MemoryStream(compiled.Symbols);
            var script = loadContext.LoadFromStream(assemblyInput, symbolsInput);
#endif
            var method = script.GetTypes().SingleOrDefault(type => type.Name == "Script" && type.IsPublic)
                ?.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
            if (method is null || method.ReturnType != typeof(object) ||
                method.GetParameters() is not [{ ParameterType: var parameterType }] || parameterType != typeof(ScriptContext))
                throw new InvalidOperationException("Script must declare public static object Execute(ScriptContext ctx).");
            object? returned;
            try
            {
                ScriptContext.Current = action.TransactionMode == "group" ? context : null;
                returned = method.Invoke(null, [context]);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
            if (context.CloseOpenScopes() || action.TransactionMode == "group" && document!.IsModifiable)
                throw new InvalidOperationException("The script left a transaction or edit scope open. The run was rolled back.");
            var limited = CodeResultLimiter.Limit(returned, ConvertSpecial);
            result.ReturnValue = limited.Value;
            result.ResultCompleteness = limited;
            if (limited.FullJson is not null)
                result.ResultArtifact = Convert.ToBase64String(Encoding.UTF8.GetBytes(limited.FullJson));
            result.Log = context.Lines.ToList();
            if (group is not null)
            {
                if (action.DryRun || action.ToolMode == "read" && action.ToolName is not null)
                {
                    if (transaction is not null && transaction.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException(failures.Message ?? "Could not roll back the dry run.");
                    if (group.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("Could not roll back the action transaction group.");
                    result.RolledBack = true;
                }
                else
                {
                    if (transaction is not null && transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException(failures.Message ?? "The action transaction was rolled back.");
                    if (changedElementCount == 0)
                    {
                        if (group.RollBack() != TransactionStatus.RolledBack)
                            throw new InvalidOperationException("Could not roll back the empty action transaction group.");
                        result.RolledBack = true;
                        result.Committed = false;
                        result.Summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
                        {
                            Command = "execute-code",
                            DocumentTitle = documentTitle,
                            NoChanges = true
                        });
                    }
                    else
                    {
                        var undoName = action.ToolName is null
                            ? ActionSummaryBuilder.BuildGroupName(clientName, "Execute code")
                            : "Model MCP: " + action.ToolName;
                        group.SetName(undoName);
                        if (group.Assimilate() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Could not assimilate the action transaction group.");
                        result.UndoName = undoName;
                        result.Committed = true;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            context.CloseOpenScopes();
            if (transaction?.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            if (group?.GetStatus() == TransactionStatus.Started) group.RollBack();
            result.RolledBack = action.TransactionMode is "auto" or "group";
            result.CodeError = exception.GetType().Name + ": " + exception.Message;
            result.Summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
            {
                Command = "execute-code",
                DocumentTitle = documentTitle,
                CodeFailure = CodeFailureKind.Execution
            });
            result.ExceptionType = exception.GetType().FullName;
            result.StackTrace = (exception.StackTrace ?? string.Empty).Split(['\n'])
                .Select(line => line.Trim()).Where(line => line.Contains("Script.Execute") || line.Contains("submitted.cs"))
                .ToList();
            result.Log = context.Lines.ToList();
        }
        finally
        {
            ScriptContext.Current = null;
            if (counting) application.Application.DocumentChanged -= CountChanges;
            transaction?.Dispose();
            group?.Dispose();
#if !NETFRAMEWORK
            loadContext.Unload();
#endif
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        }
        if (action.ToolName is not null) result.Summary = $"{action.ToolName}: {result.Summary}";
        return result;
    }

    private sealed class ScopeTrackingRewriter(SemanticModel model) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (node.Expression is not MemberAccessExpressionSyntax member || member.Name.Identifier.ValueText != "Start")
                return visited;
            if (!IsScope(model.GetTypeInfo(member.Expression).Type)) return visited;
            var visitedMember = (MemberAccessExpressionSyntax)visited.Expression;
            return visited.WithExpression(visitedMember.WithExpression(Track(visitedMember.Expression)));
        }

        public override SyntaxNode? VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
        {
            var visited = (ConditionalAccessExpressionSyntax)base.VisitConditionalAccessExpression(node)!;
            if (node.WhenNotNull is InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax member }
                && member.Name.Identifier.ValueText == "Start" && IsScope(model.GetTypeInfo(node.Expression).Type))
                return visited.WithExpression(Track(visited.Expression));
            return visited;
        }

        private static bool IsScope(ITypeSymbol? type)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (current.ToDisplayString() is "Autodesk.Revit.DB.Transaction" or "Autodesk.Revit.DB.SubTransaction"
                    or "Autodesk.Revit.DB.TransactionGroup" or "Autodesk.Revit.DB.EditScope") return true;
            }
            return false;
        }

        private static ExpressionSyntax Track(ExpressionSyntax scope) => SyntaxFactory.InvocationExpression(
            SyntaxFactory.ParseExpression("global::RevitModelMcp.Control.ScriptContext.TrackScope"),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(scope))));
    }

    private static IEnumerable<Assembly> GetBoundAssemblies() =>
        new[] { typeof(ScriptContext).Assembly, typeof(Document).Assembly, typeof(UIApplication).Assembly,
            typeof(Enumerable).Assembly, typeof(Nice3point.Revit.Extensions.FilteredElementCollectorExtensions).Assembly }.Distinct();

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var extensionType = typeof(Nice3point.Revit.Extensions.FilteredElementCollectorExtensions);
        var bound = GetBoundAssemblies().ToList();
        CodeSource.AssemblyReference Describe(Assembly assembly) => new(assembly.FullName!, assembly.Location,
            assembly.GetType(extensionType.FullName!) is not null);
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(Describe).ToList();
#if !NETFRAMEWORK
        references.AddRange(((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator).Where(File.Exists)
            .Select(path => new CodeSource.AssemblyReference(AssemblyName.GetAssemblyName(path).FullName!, path)));
#endif
        return CodeSource.SelectReferences(references, bound.Select(Describe).ToList())
            .Select(reference => MetadataReference.CreateFromFile(reference.Location));
    }

    private static object? ConvertSpecial(object value) => value switch
    {
        ElementId id => GetId(id),
        Element element => new Dictionary<string, object?>
        {
            ["id"] = GetId(element.Id),
            ["uniqueId"] = element.UniqueId,
            ["category"] = element.Category?.Name,
            ["name"] = element.Name
        },
        XYZ point => new Dictionary<string, object?>
        {
            ["x_mm"] = point.X * 304.8,
            ["y_mm"] = point.Y * 304.8,
            ["z_mm"] = point.Z * 304.8
        },
        _ => value
    };

    private static long GetId(ElementId id)
    {
#if REVIT2024_OR_GREATER
        return id.Value;
#else
        return id.IntegerValue;
#endif
    }

    private static void WriteAudit(string code, string clientName, string? documentTitle, string? documentPath,
        string mode, string? jobId)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitModelMcp", "code");
        Directory.CreateDirectory(directory);
        var safeId = Regex.Replace(jobId ?? Guid.NewGuid().ToString("N"), "[^A-Za-z0-9_-]", "");
        var path = Path.Combine(directory, DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + safeId + ".cs");
        static string Header(string? value) => (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("*/", "* /");
        if (Environment.GetEnvironmentVariable("REVIT_MCP_REDACT_PATHS") == "1")
        {
            if (!string.IsNullOrEmpty(documentPath))
                code = code.Replace(documentPath!, "[redacted model path]")
                    .Replace(documentPath!.Replace("\\", "\\\\"), "[redacted model path]");
            code = Regex.Replace(code, @"(?:[A-Za-z]:\\|\\\\)[^""'\r\n]+", "[redacted path]");
        }
        File.WriteAllText(path, $"/* Client: {Header(clientName)}; Document: {Header(documentTitle)}; Transaction: {mode} */\n" + code);
        foreach (var oldFile in Directory.GetFiles(directory, "*.cs").OrderByDescending(File.GetCreationTimeUtc).Skip(500))
            File.Delete(oldFile);
    }
}
