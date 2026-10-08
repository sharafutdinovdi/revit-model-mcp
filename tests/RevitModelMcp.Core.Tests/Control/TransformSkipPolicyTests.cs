using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class TransformSkipPolicyTests
{
    [Test]
    [Arguments("move", false, true)]
    [Arguments("rotate", false, true)]
    [Arguments("mirror", false, true)]
    [Arguments("mirror", true, false)]
    [Arguments("copy", false, false)]
    [Arguments("change-type", false, false)]
    public async Task Supports_CoversMoveRotateAndInPlaceMirror(string command, bool copy, bool expected)
    {
        await Assert.That(TransformSkipPolicy.Supports(command, copy)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(true, true, TransformSkipReason.Pinned)]
    [Arguments(true, false, TransformSkipReason.Pinned)]
    [Arguments(false, true, TransformSkipReason.InGroup)]
    public async Task ClassifyBefore_ReportsPinnedBeforeGroup(bool pinned, bool inGroup, TransformSkipReason expected)
    {
        await Assert.That(TransformSkipPolicy.ClassifyBefore(pinned, inGroup)).IsEqualTo(expected);
    }

    [Test]
    public async Task ClassifyBefore_FreeElementIsNotSkipped()
    {
        await Assert.That(TransformSkipPolicy.ClassifyBefore(false, false)).IsNull();
    }

    [Test]
    [Arguments(true, TransformSkipReason.Hosted)]
    [Arguments(false, TransformSkipReason.Constrained)]
    public async Task ClassifyUnchanged_SeparatesHostedFromConstrained(bool hosted, TransformSkipReason expected)
    {
        await Assert.That(TransformSkipPolicy.ClassifyUnchanged(hosted)).IsEqualTo(expected);
    }

    [Test]
    public async Task SamePose_ComparesWithinTolerance()
    {
        await Assert.That(TransformSkipPolicy.SamePose([1, 2, 3], [1, 2, 3.0004])).IsTrue();
        await Assert.That(TransformSkipPolicy.SamePose([1, 2, 3], [1, 2, 3.5])).IsFalse();
        await Assert.That(TransformSkipPolicy.SamePose([1, 2], [1, 2, 3])).IsFalse();
        await Assert.That(TransformSkipPolicy.SamePose(null, null)).IsTrue();
        await Assert.That(TransformSkipPolicy.SamePose(null, [1])).IsFalse();
    }

    [Test]
    public async Task Add_FillsTheListForEachReason()
    {
        var skipped = new SkippedByReason();
        TransformSkipPolicy.Add(skipped, TransformSkipReason.Pinned, 1);
        TransformSkipPolicy.Add(skipped, TransformSkipReason.InGroup, 2);
        TransformSkipPolicy.Add(skipped, TransformSkipReason.Hosted, 3);
        TransformSkipPolicy.Add(skipped, TransformSkipReason.Constrained, 4);
        await Assert.That(skipped.Pinned!.SequenceEqual([1L])).IsTrue();
        await Assert.That(skipped.InGroup.SequenceEqual([2L])).IsTrue();
        await Assert.That(skipped.Hosted!.SequenceEqual([3L])).IsTrue();
        await Assert.That(skipped.Constrained!.SequenceEqual([4L])).IsTrue();
        await Assert.That(TransformSkipPolicy.Total(skipped)).IsEqualTo(4);
    }

    [Test]
    public async Task Warning_NamesEveryReasonAndKeepsTheGroupText()
    {
        var skipped = new SkippedByReason { InGroup = [2], Pinned = [1, 5], Hosted = [3], Constrained = [4] };
        await Assert.That(TransformSkipPolicy.Warning(skipped)).IsEqualTo(
            GroupSkipPolicy.SkipWarning(1) +
            " 2 elements were skipped because they are pinned; unpin in Revit to change them." +
            " 1 element was skipped because Revit kept it on the host and it did not change." +
            " 1 element was skipped because a constraint, such as a curtain wall grid, kept it in place.");
    }

    [Test]
    public async Task Warning_IsEmptyWithoutSkips()
    {
        await Assert.That(TransformSkipPolicy.Warning(new SkippedByReason())).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task NoneChangedMessage_ListsCountsPerReason()
    {
        var skipped = new SkippedByReason { InGroup = [2], Pinned = [1], Hosted = [3] };
        await Assert.That(TransformSkipPolicy.NoneChangedMessage("move", 3, skipped)).IsEqualTo(
            "None of the 3 requested elements could be moved (1 pinned, 1 in groups, 1 hosted and unchanged). Nothing was changed.");
        await Assert.That(TransformSkipPolicy.NoneChangedMessage("rotate", 1, new SkippedByReason { Constrained = [4] })).IsEqualTo(
            "None of the 1 requested elements could be rotated (1 constrained and unchanged). Nothing was changed.");
    }

    [Test]
    public async Task SkippedByReason_OmitsNewReasonsWhenEmpty()
    {
        var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(SkippedByReason));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, new SkippedByReason { Pinned = [9] });
        var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        await Assert.That(json).IsEqualTo("{\"missing\":[],\"readOnly\":[],\"typeParameter\":[],\"inGroup\":[],\"pinned\":[9]}");
    }
}
