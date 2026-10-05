using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ViewTemplateRefusalTests
{
    [Test]
    public async Task Message_ListsQuotedViews() =>
        await Assert.That(ViewTemplateRefusal.Message("Plan T", ["Level 1", "Level 2"]))
            .IsEqualTo("Template 'Plan T' was not applied: it does not match the type of 'Level 1', 'Level 2'.");
}
