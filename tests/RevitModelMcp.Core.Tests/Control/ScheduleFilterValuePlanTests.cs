using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ScheduleFilterValuePlanTests
{
    [Test]
    public async Task ElementIdFieldFiltersByNameFirst()
    {
        var plan = ScheduleFilterValuePlan.Plan("ElementId", "Level 1");
        await Assert.That(plan.SequenceEqual([ScheduleFilterValueKind.ElementName])).IsTrue();
    }

    [Test]
    public async Task ElementIdFieldAcceptsNumericTextAsNameOrRawId()
    {
        var plan = ScheduleFilterValuePlan.Plan("ElementId", "12");
        await Assert.That(plan.SequenceEqual([ScheduleFilterValueKind.ElementName, ScheduleFilterValueKind.ElementIdLiteral])).IsTrue();
    }

    [Test]
    public async Task StringFieldUsesTextOnly()
    {
        var plan = ScheduleFilterValuePlan.Plan("String", "12");
        await Assert.That(plan.SequenceEqual([ScheduleFilterValueKind.Text])).IsTrue();
    }

    [Test]
    public async Task NumericFieldKeepsTextNumberIntegerOrder()
    {
        var plan = ScheduleFilterValuePlan.Plan("Double", "800");
        await Assert.That(plan.SequenceEqual([
            ScheduleFilterValueKind.Text, ScheduleFilterValueKind.Number, ScheduleFilterValueKind.Integer])).IsTrue();
    }

    [Test]
    public async Task UnknownStorageTriesNameLast()
    {
        var plan = ScheduleFilterValuePlan.Plan(null, "Level 1");
        await Assert.That(plan.SequenceEqual([ScheduleFilterValueKind.Text, ScheduleFilterValueKind.ElementName])).IsTrue();
    }

    [Test]
    public async Task ResolveName_UniqueMatchReturnsId()
    {
        var result = ScheduleFilterValuePlan.ResolveName([(1, "Level 1"), (2, "Level 2"), (1, "Level 1")], " level 1 ");
        await Assert.That(result.Id).IsEqualTo(1L);
        await Assert.That(result.Error).IsNull();
    }

    [Test]
    public async Task ResolveName_NoneAndAmbiguousReportErrors()
    {
        var none = ScheduleFilterValuePlan.ResolveName([(1, "Level 1")], "Roof");
        var many = ScheduleFilterValuePlan.ResolveName([(1, "A"), (2, "A")], "A");
        await Assert.That(none.Id).IsNull();
        await Assert.That(none.Error).Contains("no element named 'Roof'");
        await Assert.That(many.Id).IsNull();
        await Assert.That(many.Error).Contains("2 elements");
    }
}
