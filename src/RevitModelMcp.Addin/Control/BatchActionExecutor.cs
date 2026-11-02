using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Output;

namespace RevitModelMcp.Control;

internal static class BatchActionExecutor
{
    internal static ActionResultData Execute(Document document, UIDocument? uiDocument, ActionJobContract action,
        ActionCommandExecutor.ActionFailures failures, string clientName)
    {
        var selection = uiDocument?.Selection.GetElementIds();
        var result = new ActionResultData
        {
            DryRun = action.DryRun,
            Steps = [],
            Committed = false
        };
        using var group = new TransactionGroup(document, "MCP batch");
        if (group.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Could not start the batch transaction group.");
        // Free instance text writes share one regeneration and commit per batch. Value checks and
        // verification snapshots run per step. Group, type, geometry, and preview steps retain preflight.
        using var parameterTransaction = !action.DryRun && action.Steps.All(step =>
            ActionPreflight.CanShareParameterTransaction(document, step.Command, step.Action!))
            ? new Transaction(document, "revit_set_parameter") : null;
        try
        {
            if (parameterTransaction is not null)
            {
                if (parameterTransaction.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Could not start the batch parameter transaction.");
                parameterTransaction.SetFailureHandlingOptions(parameterTransaction.GetFailureHandlingOptions()
                    .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
            }
            foreach (var step in action.Steps)
            {
                var entry = new BatchStepResult { Index = result.Steps.Count, Command = step.Command };
                result.Steps.Add(entry);
                try
                {
                    var stepAction = step.Action!;
                    var originalDryRun = stepAction.DryRun;
                    try
                    {
                        stepAction.DryRun |= action.DryRun;
                        // Dry runs must never commit: some deletions are irreversible.
                        entry.Data = parameterTransaction is not null
                            ? SetParameter(document, stepAction)
                            : ActionCommandExecutor.ExecuteStep(document, uiDocument, step.Command, stepAction,
                                failures, clientName, out _, wrapGroup: false);
                    }
                    finally
                    {
                        stepAction.DryRun = originalDryRun;
                    }
                    entry.Success = true;
                }
                catch (Exception exception)
                {
                    entry.Error = $"Step {entry.Index} ({entry.Command}) failed: {exception.Message}";
                    result.FailedStep = entry.Index;
                    RollBack();
                    return result;
                }
                finally
                {
                    result.PublishBatchProgress(action.Steps.Count, step.Command, ResponseDelivery.Current);
                }
            }
            if (parameterTransaction is not null)
            {
                document.Regenerate();
                if (parameterTransaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException(failures.Message ?? "The batch parameter transaction was rolled back.");
                foreach (var entry in result.Steps)
                {
                    var data = entry.Data!;
                    data.Verification!.After = null;
                    try
                    {
                        ActionVerifier.CaptureAfter(document, "set-parameter", action.Steps[entry.Index].Action!, data);
                    }
                    catch (Exception exception)
                    {
                        data.Verification.Error = "Post-commit verification failed: " + exception.Message;
                        PluginLog.Error(data.Verification.Error, exception);
                    }
                }
            }
            var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
            {
                Command = "batch",
                DocumentTitle = document.Title,
                DryRun = action.DryRun,
                BatchStepCount = result.Steps.Count
            });
            result.Summary = summary;
            if (action.DryRun)
            {
                // Dry runs must never commit: some deletions are irreversible.
                RollBack();
            }
            else
            {
                var groupName = ActionSummaryBuilder.BuildGroupName(clientName, summary);
                group.SetName(groupName);
                if (group.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Could not assimilate the batch transaction group.");
                result.Committed = true;
                result.UndoName = groupName;
            }
            return result;
        }
        catch (Exception exception)
        {
            if (group.GetStatus() is TransactionStatus.Started or TransactionStatus.RolledBack) RollBack();
            result.FailedStep = Math.Max(0, result.Steps.Count - 1);
            if (result.Steps.Count > 0)
            {
                result.Steps.Last().Success = false;
                result.Steps.Last().Error = $"Step {result.FailedStep} ({result.Steps.Last().Command}) failed at batch completion: {exception.Message}";
            }
            return result;
        }

        void RollBack()
        {
            if (parameterTransaction?.GetStatus() == TransactionStatus.Started && parameterTransaction.RollBack() != TransactionStatus.RolledBack)
                throw new InvalidOperationException("Could not roll back the batch parameter transaction.");
            if (group.GetStatus() != TransactionStatus.RolledBack && group.RollBack() != TransactionStatus.RolledBack)
                throw new InvalidOperationException("Could not roll back the batch transaction group.");
            result.RolledBack = true;
            foreach (var entry in result.Steps)
            {
                entry.RolledBack = true;
                if (entry.Data is not null) entry.Data.RolledBack = true;
            }
            if (selection is not null) uiDocument!.Selection.SetElementIds(selection);
        }
    }

    private static ActionResultData SetParameter(Document document, ActionJobContract action)
    {
        if (Policy.PolicyProvider.Refusal("set-parameter") is string refusal) throw new InvalidOperationException(refusal);
        var before = ActionVerifier.CaptureBefore(document, "set-parameter", action, []);
        var data = ActionMutations.SetParameter(document, action);
        data.DryRun = false;
        data.Verification = new ActionVerification { Before = before };
        ActionVerifier.CaptureAfter(document, "set-parameter", action, data);
        data.Summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "set-parameter",
            DocumentTitle = document.Title,
            Parameter = action.Parameter
        });
        return data;
    }
}
