using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Core.Tests.Batch;

public sealed class BatchDialogPolicyTests
{
    [Test]
    public async Task Decide_DeniesUnknownIdOrRuntimeType()
    {
        await Assert.That(BatchDialogPolicy.Decide("unknown", "TaskDialogShowingEventArgs").Recycle).IsTrue();
        await Assert.That(BatchDialogPolicy.Decide("TaskDialog_Missing_Third_Party_Updater", "DialogBoxShowingEventArgs").Recycle).IsTrue();
    }

    [Test]
    public async Task Decide_OverridesOnlyConfiguredPair()
    {
        var choices = new Dictionary<(string Id, string Type), int>
        {
            [("known", "TaskDialogShowingEventArgs")] = 1002
        };
        await Assert.That(BatchDialogPolicy.Decide("known", "TaskDialogShowingEventArgs", choices).Allowed).IsTrue();
        await Assert.That(BatchDialogPolicy.Decide("known", "DialogBoxShowingEventArgs", choices).Recycle).IsTrue();
    }

}
