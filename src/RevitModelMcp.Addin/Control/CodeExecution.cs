using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
    private sealed record CompiledCode(byte[] Assembly, byte[] Symbols);
    private static readonly Dictionary<string, CompiledCode> Cache = [];
    private static readonly Queue<string> CacheOrder = [];

    public static ActionResultData Execute(UIApplication application, Document? document, UIDocument? uiDocument,
        ActionJobContract action, ActionCommandExecutor.ActionFailures failures, string clientName, string? jobId)
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
                Command = "execute-code", DocumentTitle = documentTitle, DryRun = action.DryRun
            })
        };
        if (action.TransactionMode == "none")
            result.Warning = "The script owns transactions and document lifecycle. Undo is not guaranteed.";
        WriteAudit(code, clientName, document?.Title, document?.PathName, action.TransactionMode, jobId);
        var key = CodeSource.CacheKey(code, action.TransactionMode);
        if (!Cache.TryGetValue(key, out var compiled))
        {
            var source = CodeSource.BuildSource(code);
            var references = GetReferences();
            var compilation = CSharpCompilation.Create("McpScript_" + key.Substring(0, 16),
                [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release));
            using var assemblyStream = new MemoryStream();
            using var symbolsStream = new MemoryStream();
            var emitted = compilation.Emit(assemblyStream, symbolsStream,
                options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            if (!emitted.Success)
            {
                result.CodeError = "compilation failed";
                result.Summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
                {
                    Command = "execute-code", DocumentTitle = documentTitle, CodeFailure = CodeFailureKind.Compilation
                });
                result.Diagnostics = emitted.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)
                    .Select(item =>
                    {
                        var position = item.Location.GetMappedLineSpan().StartLinePosition;
                        return new CodeDiagnostic
                        {
                            Line = Math.Max(1, position.Line + 1), Column = Math.Max(1, position.Character + 1),
                            Id = item.Id, Message = item.GetMessage()
                        };
                    }).ToList();
                result.ElapsedMs = stopwatch.ElapsedMilliseconds;
                return result;
            }
            compiled = new CompiledCode(assemblyStream.ToArray(), symbolsStream.ToArray());
            Cache[key] = compiled;
            CacheOrder.Enqueue(key);
            if (CacheOrder.Count > 32) Cache.Remove(CacheOrder.Dequeue());
        }

        var context = new ScriptContext(application, uiDocument, document);
        TransactionGroup? group = null;
        Transaction? transaction = null;
#if !NETFRAMEWORK
        var loadContext = new AssemblyLoadContext("McpScript_" + key.Substring(0, 12), isCollectible: true);
        loadContext.Resolving += (_, name) => AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(item => item.GetName().Name == name.Name);
#endif
        try
        {
            if (action.TransactionMode == "auto")
            {
                if (document is null) throw new InvalidOperationException("No active Revit document.");
                group = new TransactionGroup(document, "MCP action");
                if (group.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Could not start the action transaction group.");
                transaction = new Transaction(document, "revit_execute_code");
                if (transaction.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Could not start the action transaction.");
                transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
                    .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
            }
#if NETFRAMEWORK
            var script = Assembly.Load(compiled.Assembly, compiled.Symbols);
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
                returned = method.Invoke(null, [context]);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
            result.ReturnValue = CodeResultLimiter.Limit(returned, ConvertSpecial);
            result.Log = context.Lines.ToList();
            if (transaction is not null)
            {
                if (action.DryRun)
                {
                    if (transaction.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException(failures.Message ?? "Could not roll back the dry run.");
                    result.RolledBack = true;
                    group!.RollBack();
                }
                else
                {
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException(failures.Message ?? "The action transaction was rolled back.");
                    var undoName = ActionSummaryBuilder.BuildGroupName(clientName, "Execute code");
                    group!.SetName(undoName);
                    if (group.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Could not assimilate the action transaction group.");
                    result.UndoName = undoName;
                    result.Committed = true;
                }
            }
        }
        catch (Exception exception)
        {
            if (transaction?.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            if (group?.GetStatus() == TransactionStatus.Started) group.RollBack();
            result.RolledBack = action.TransactionMode == "auto";
            result.CodeError = exception.GetType().Name + ": " + exception.Message;
            result.Summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
            {
                Command = "execute-code", DocumentTitle = documentTitle, CodeFailure = CodeFailureKind.Execution
            });
            result.ExceptionType = exception.GetType().FullName;
            result.StackTrace = (exception.StackTrace ?? string.Empty).Split(['\n'])
                .Select(line => line.Trim()).Where(line => line.Contains("Script.Execute") || line.Contains("submitted.cs"))
                .ToList();
            result.Log = context.Lines.ToList();
        }
        finally
        {
            transaction?.Dispose();
            group?.Dispose();
#if !NETFRAMEWORK
            loadContext.Unload();
#endif
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        }
        return result;
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var paths = new List<string>();
#if !NETFRAMEWORK
        paths.AddRange(((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator).Where(File.Exists));
#endif
        paths.AddRange(AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly => assembly.Location));
        paths.Add(typeof(ScriptContext).Assembly.Location);
        paths.Add(typeof(Document).Assembly.Location);
        paths.Add(typeof(UIApplication).Assembly.Location);
        paths.Add(typeof(Enumerable).Assembly.Location);
        return paths.GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(path => MetadataReference.CreateFromFile(path));
    }

    private static object? ConvertSpecial(object value) => value switch
    {
        ElementId id => GetId(id),
        Element element => new Dictionary<string, object?>
        {
            ["id"] = GetId(element.Id), ["uniqueId"] = element.UniqueId,
            ["category"] = element.Category?.Name, ["name"] = element.Name
        },
        XYZ point => new Dictionary<string, object?>
        {
            ["x_mm"] = point.X * 304.8, ["y_mm"] = point.Y * 304.8, ["z_mm"] = point.Z * 304.8
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
