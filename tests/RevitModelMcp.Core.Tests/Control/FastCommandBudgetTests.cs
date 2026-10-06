using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class FastCommandBudgetTests
{
    [Test]
    [Arguments("export-view", 59_999L, false)]
    [Arguments("export-view", 70_000L, false)]
    [Arguments("capture-elements", 120_000L, false)]
    [Arguments("query-elements", 59_999L, false)]
    [Arguments("query-elements", 60_000L, true)]
    [Arguments("query-elements", 70_000L, true)]
    [Arguments("list-views", 59_999L, false)]
    [Arguments("list-views", 60_000L, true)]
    [Arguments("list-views", 70_000L, true)]
    public async Task IsPartialAfterBudget_DependsOnCommandAndElapsedTime(string command, long elapsedMs, bool expected)
    {
        await Assert.That(FastCommandBudget.IsPartialAfterBudget(command, elapsedMs)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("export-view", true)]
    [Arguments("capture-elements", true)]
    [Arguments("query-elements", false)]
    [Arguments("list-views", false)]
    [Arguments("document-info", false)]
    public async Task IsImageCommand_RecognizesImageProducingCommands(string command, bool expected)
    {
        await Assert.That(FastCommandBudget.IsImageCommand(command)).IsEqualTo(expected);
    }
}
