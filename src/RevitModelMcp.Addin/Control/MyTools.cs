using Autodesk.Revit.UI;
using RevitModelMcp.Activity;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Output;
using RevitModelMcp.Policy;

namespace RevitModelMcp.Control;

internal static class MyTools
{
    internal static ToolSession Session { get; } = new();

    internal static ToolStore Store { get; } = new(ToolStore.DefaultDirectory);

    internal static void PrepareRun(ActionJobContract action)
    {
        var tool = Store.Get(action.Name ?? throw new ArgumentException("name is required."));
        action.Args = ToolArguments.Validate(tool.Parameters, action.ToolArgs);
        action.ToolConfirmationText = ToolSession.RunText(tool, action.Args);
        action.ToolName = tool.Name;
        action.ToolRevision = tool.Revision;
        action.ToolMode = tool.Mode;
        action.ToolApprovalBinding = tool.ApprovalBinding();
        action.Code = tool.Code;
        action.TransactionMode = "auto";
    }

    internal static void ExecuteCatalog(UIApplication application, ControlJobParseResult job, DateTimeOffset startedAt)
    {
        ActionResultData? data = null;
        CommandResponse<ActionResultData> response;
        try
        {
            if (PolicyProvider.Refusal(job.Command) is string refusal) throw new InvalidOperationException(refusal);
            if (job.Command is not ("list-tools" or "get-tool") && ActionCommandExecutor.ReadOnlyMode)
                throw new InvalidOperationException(PolicyProvider.ReadOnlyMessage);
            if (job.Error is not null) throw new ArgumentException(job.Error);
            var action = job.Action ?? throw new ArgumentException("Missing tool arguments.");
            data = job.Command switch
            {
                "list-tools" => List(),
                "get-tool" => new ActionResultData { Tool = Store.Get(action.Name ?? ""), Summary = "Retrieved saved tool for review." },
                "save-tool" => Save(application, action, job),
                "delete-tool" => Delete(action),
                _ => throw new ArgumentException("Unknown tool command.")
            };
            response = data.CodeError is null
                ? CommandResponse<ActionResultData>.Ok(job.Command, data, 0)
                : CommandResponse<ActionResultData>.Fail(job.Command, data.CodeError, 0);
            response.Data = data;
        }
        catch (Exception exception)
        {
            response = CommandResponse<ActionResultData>.Fail(job.Command, exception.Message, 0);
        }
        if (data?.NeedsConfirmation != true) ActivityRecorder.RecordAction(job, null, data, response, null);
        CommandResponseFileWriter.Create(startedAt.LocalDateTime, job.Command,
            ReadCommandReader.ReadResponder(application), job.CorrelationId).Write(response);
    }

    private static ActionResultData List()
    {
        var tools = Store.List(out var invalid);
        return new ActionResultData { Tools = tools.Select(tool => tool with { Code = "" }).ToList(), Invalid = invalid, Summary = "Listed saved tools." };
    }

    private static ActionResultData Save(UIApplication application, ActionJobContract action, ControlJobParseResult job)
    {
        ToolDefinition? previous = null;
        try { previous = Store.Get(action.Name ?? ""); }
        catch (InvalidOperationException) { }
        var code = ToolDefinition.Normalize(action.Code ?? "");
        var definition = new ToolDefinition
        {
            Name = action.Name ?? "",
            Description = action.ToolDescription ?? "",
            Parameters = action.ToolParameters ?? "",
            Code = code,
            Mode = action.ToolMode,
            TestedRevitYears = action.ToolYears.Distinct().OrderBy(year => year).ToArray(),
            Created = DateTimeOffset.UtcNow.ToString("O"),
            Author = job.ClientName,
            Revision = ToolDefinition.Hash(code),
            ReplacedRevision = previous?.Revision ?? ""
        };
        definition.Validate();
        action.Code = code;
        action.ToolRevision = definition.Revision;
        var compilation = CodeExecution.Execute(application, null, null, action,
            new ActionCommandExecutor.ActionFailures(), job.ClientName, job.JobId, compileOnly: true);
        if (compilation.CodeError is not null) return compilation;
        var confirmation = ConfirmationStore.Gate("save-tool", definition.Name, definition.ApprovalBinding(), "",
            action.ConfirmToken,
            ToolSession.SaveText(definition, previous),
            $"Needs confirmation to save {definition.Name} ({definition.Revision}).", "The tool definition changed. Review it again.");
        if (confirmation is not null)
        {
            confirmation.Tool = definition;
            return confirmation;
        }
        Store.Save(definition);
        return new ActionResultData { Tool = definition, Summary = $"Saved {definition.Name} ({definition.Revision})." };
    }

    private static ActionResultData Delete(ActionJobContract action)
    {
        var name = action.Name ?? "";
        var binding = Store.DeletionBinding(name);
        var confirmation = ConfirmationStore.Gate("delete-tool", name, binding, "",
            action.ConfirmToken, $"Delete saved tool {name} and all its revisions? This cannot be undone.",
            $"Needs confirmation to delete {name}.", "The tool changed. Review it again.");
        if (confirmation is not null) return confirmation;
        Store.Delete(name, binding);
        return new ActionResultData { Summary = $"Deleted saved tool {name}." };
    }
}
