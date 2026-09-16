using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class BatchActionExecutor
{
    internal static ActionResultData Execute(Document document, UIDocument? uiDocument, ActionJobContract action,
        ActionCommandExecutor.ActionFailures failures)
    {
        var selection = uiDocument?.Selection.GetElementIds();
        var result = new ActionResultData
        {
            DryRun = action.DryRun,
            Steps = [],
            UndoName = "revit_batch",
            Committed = false
        };
        using var group = new TransactionGroup(document, "revit_batch");
        if (group.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Could not start the batch transaction group.");
        try
        {
            foreach (var step in action.Steps)
            {
                var entry = new BatchStepResult { Index = result.Steps.Count, Command = step.Command };
                result.Steps.Add(entry);
                try
                {
                    // Batch previews retain earlier changes until the entire group rolls back.
                    var stepAction = step.Action!;
                    var originalDryRun = stepAction.DryRun;
                    try
                    {
                        stepAction.DryRun |= action.DryRun;
                        entry.Data = ActionCommandExecutor.ExecuteStep(document, uiDocument, step.Command, stepAction,
                            failures, out _, deferDryRun: action.DryRun);
                    }
                    finally
                    {
                        stepAction.DryRun = originalDryRun;
                    }
                    entry.Success = true;
                }
                catch (Exception exception)
                {
                    entry.Error = exception.Message;
                    result.FailedStep = entry.Index;
                    RollBack();
                    return result;
                }
            }
            if (action.DryRun) RollBack();
            else
            {
                if (group.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Could not assimilate the batch transaction group.");
                result.Committed = true;
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
                result.Steps.Last().Error = exception.Message;
            }
            return result;
        }

        void RollBack()
        {
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
}
