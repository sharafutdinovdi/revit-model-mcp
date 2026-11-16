using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class CodeExecutionConfirmationTests
{
    private static ActionJobContract Action(string command, bool dryRun) => new()
    {
        Code = "return 1;",
        TransactionMode = "auto",
        DryRun = dryRun,
        ToolName = command == "run-tool" ? "my_tool" : null,
        ToolRevision = command == "run-tool" ? "revision" : null,
        ToolApprovalBinding = command == "run-tool" ? "approval" : null,
        ToolArgs = "{\"count\":1}"
    };

    [Test]
    [Arguments("run-tool", true)]
    [Arguments("run-tool", false)]
    [Arguments("execute-code", true)]
    [Arguments("execute-code", false)]
    public async Task Tokens_RejectChangedExecutionIntentAndConsumeToken(string command, bool dryRun)
    {
        var tokens = new DocumentConfirmationTokens();
        var original = ToolArguments.ExecutionBinding(Action(command, dryRun));
        var changed = ToolArguments.ExecutionBinding(Action(command, !dryRun));
        var token = tokens.Issue(command, "document", original, "");

        await Assert.That(tokens.Consume(token, command, "document", changed, ""))
            .IsEqualTo(DocumentConfirmationResult.ArgumentsMismatch);
        await Assert.That(tokens.Consume(token, command, "document", original, ""))
            .IsEqualTo(DocumentConfirmationResult.Invalid);
    }

    [Test]
    [Arguments("run-tool", true)]
    [Arguments("run-tool", false)]
    [Arguments("execute-code", true)]
    [Arguments("execute-code", false)]
    public async Task Tokens_AcceptMatchingExecutionIntentOnlyOnce(string command, bool dryRun)
    {
        var tokens = new DocumentConfirmationTokens();
        var arguments = ToolArguments.ExecutionBinding(Action(command, dryRun));
        var token = tokens.Issue(command, "document", arguments, "");
        var matching = ToolArguments.ExecutionBinding(Action(command, dryRun));

        await Assert.That(tokens.Consume(token, command, "document", matching, ""))
            .IsEqualTo(DocumentConfirmationResult.Valid);
        await Assert.That(tokens.Consume(token, command, "document", matching, ""))
            .IsEqualTo(DocumentConfirmationResult.Invalid);
    }

    [Test]
    public async Task ExecutionBinding_PreservesCodeAndSavedToolArguments()
    {
        var original = ToolArguments.ExecutionBinding(Action("run-tool", true));
        Action<ActionJobContract>[] changes =
        [
            action => action.Code = "return 2;",
            action => action.TransactionMode = "group",
            action => action.ToolName = "other_tool",
            action => action.ToolRevision = "other-revision",
            action => action.ToolApprovalBinding = "other-approval",
            action => action.ToolArgs = "{\"count\":2}"
        ];
        foreach (var change in changes)
        {
            var action = Action("run-tool", true);
            change(action);
            await Assert.That(ToolArguments.ExecutionBinding(action)).IsNotEqualTo(original);
        }
    }

    [Test]
    [Arguments("execute-code")]
    [Arguments("run-tool")]
    public async Task ExecutionBinding_PreservesUnlimitedCallsAndBindsExecutionLimit(string command)
    {
        var action = Action(command, false);
        var legacy = DocumentConfirmationBinding.CodeArguments(action.Code!, action.TransactionMode) +
            $":dry_run={action.DryRun}" +
            (action.ToolName is null ? "" : $":{action.ToolName}:{action.ToolRevision}:{action.ToolApprovalBinding}:{action.ToolArgs}");
        await Assert.That(ToolArguments.ExecutionBinding(action)).IsEqualTo(legacy);
        var tokens = new DocumentConfirmationTokens();
        action.MaxSeconds = 2;
        var limited = ToolArguments.ExecutionBinding(action);
        await Assert.That(limited).IsNotEqualTo(legacy);
        var token = tokens.Issue(command, "document", limited, "");
        action.MaxSeconds = 3;
        await Assert.That(tokens.Consume(token, command, "document", ToolArguments.ExecutionBinding(action), ""))
            .IsEqualTo(DocumentConfirmationResult.ArgumentsMismatch);
    }

    [Test]
    public async Task ExecutionBinding_ExcludesConfirmationToken()
    {
        var action = Action("run-tool", true);
        var original = ToolArguments.ExecutionBinding(action);
        action.ConfirmToken = "confirmation";
        await Assert.That(ToolArguments.ExecutionBinding(action)).IsEqualTo(original);
    }
}
