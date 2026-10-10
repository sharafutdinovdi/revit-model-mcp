using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class CadLayerSelectionTests
{
    [Test]
    public async Task Missing_IgnoresCaseAndReturnsOnlyUnknownLayers()
    {
        var missing = CadLayerSelection.Missing(["WALLS", "Wals", "wals"], ["Walls", "Doors"]);
        await Assert.That(missing).IsEquivalentTo(new List<string> { "Wals" });
    }

    [Test]
    public async Task Missing_AllKnown_IsEmpty()
    {
        await Assert.That(CadLayerSelection.Missing(["a"], ["A"])).IsEmpty();
    }

    [Test]
    public async Task UnknownMessage_ListsMissingAndSortedAvailableLayers()
    {
        var message = CadLayerSelection.UnknownMessage(["Wals"], ["Walls", "Doors"]);
        await Assert.That(message).IsEqualTo("CAD layer 'Wals' not found. Available layers: Doors, Walls.");
    }

    [Test]
    public async Task UnknownMessage_PluralWithNoLayers()
    {
        var message = CadLayerSelection.UnknownMessage(["a", "b"], []);
        await Assert.That(message).IsEqualTo("CAD layers 'a', 'b' not found. Available layers: none.");
    }

    [Test]
    public async Task NoWallsMessage_MentionsUnpairedLines()
    {
        await Assert.That(CadLayerSelection.NoWallsMessage(4)).Contains("4 unpaired lines");
    }
}
