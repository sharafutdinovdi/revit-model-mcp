using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class PreflightPolicyTests
{
    [Test]
    public async Task BlockedIds_IntersectsDistinctIdsInAscendingOrder()
    {
        var blocked = GroupFailurePolicy.BlockedIds([5, 3, 3, 1, 9], [new long[] { 3, 5, 30 }, new long[] { 1, 3 }]);
        await Assert.That(string.Join(",", blocked)).IsEqualTo("1,3,5");
        await Assert.That(GroupFailurePolicy.BlockedIds([], [new long[] { 1 }]).Count).IsEqualTo(0);
        await Assert.That(GroupFailurePolicy.BlockedIds([1], []).Count).IsEqualTo(0);
        await Assert.That(GroupFailurePolicy.BlockedIds([1], [new long[] { 2 }]).Count).IsEqualTo(0);
    }

    [Test]
    public async Task BlockedIds_MatchesDirectIdsAndGroupIdsInAscendingOrder()
    {
        var groups = new Dictionary<long, long> { [5] = 50, [3] = 30, [1] = 10, [9] = 90 };
        var blocked = GroupFailurePolicy.BlockedIds([5, 3, 3, 1, 9, 7],
            [new long[] { 3, 50, 30, 7 }, new long[] { 50, 3 }],
            id => groups.TryGetValue(id, out var groupId) ? groupId : null);
        await Assert.That(string.Join(",", blocked)).IsEqualTo("3,5,7");
        await Assert.That(GroupFailurePolicy.BlockedIds([1], [new long[] { 2 }], _ => null).Count).IsEqualTo(0);
        await Assert.That(GroupFailurePolicy.BlockedIds([1], [], _ => 10).Count).IsEqualTo(0);
        await Assert.That(GroupFailurePolicy.BlockedIds([], [new long[] { 10 }], _ => 10).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("move", true)]
    [Arguments("rotate", true)]
    [Arguments("copy", true)]
    [Arguments("mirror", true)]
    [Arguments("change-type", true)]
    [Arguments("update-parameters", false)]
    [Arguments("set-parameter", false)]
    [Arguments("delete", false)]
    [Arguments("walls-from-cad", false)]
    [Arguments("", false)]
    public async Task GroupSkipPolicy_SupportsOnlyGroupActions(string command, bool expected)
    {
        await Assert.That(GroupSkipPolicy.Supports(command)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(3, 0, GroupSkipPolicy.GroupSkipDecision.Proceed)]
    [Arguments(3, 1, GroupSkipPolicy.GroupSkipDecision.SkipSome)]
    [Arguments(3, 3, GroupSkipPolicy.GroupSkipDecision.RefuseAll)]
    [Arguments(1, 1, GroupSkipPolicy.GroupSkipDecision.RefuseAll)]
    [Arguments(0, 0, GroupSkipPolicy.GroupSkipDecision.Proceed)]
    [Arguments(3, 4, GroupSkipPolicy.GroupSkipDecision.RefuseAll)]
    public async Task GroupSkipPolicy_DecidesFromRequestedAndGroupedCounts(int requested, int inGroup,
        GroupSkipPolicy.GroupSkipDecision expected)
    {
        await Assert.That(GroupSkipPolicy.Decide(requested, inGroup)).IsEqualTo(expected);
    }

    [Test]
    public async Task GroupSkipPolicy_UsesExactRefusalAndWarningText()
    {
        await Assert.That(GroupSkipPolicy.RefusalMessage(1)).IsEqualTo(
            "The element belongs to a group and Revit allows changes to group members only in group edit mode. Nothing was changed.");
        await Assert.That(GroupSkipPolicy.RefusalMessage(3)).IsEqualTo(
            "All 3 elements belong to groups and Revit allows changes to group members only in group edit mode. Nothing was changed.");
        await Assert.That(GroupSkipPolicy.SkipWarning(1)).IsEqualTo(
            "1 element was skipped because they belong to groups; Revit allows changes to group members only in group edit mode.");
        await Assert.That(GroupSkipPolicy.SkipWarning(2)).IsEqualTo(
            "2 elements were skipped because they belong to groups; Revit allows changes to group members only in group edit mode.");
    }

    [Test]
    public async Task FindCandidates_SelectsTouchingTJunctionEnd()
    {
        var candidates = WallJoinPreflight.FindCandidates([
            new(new(0, 0), new(1000, 0), 100), new(new(500, 0), new(500, 1000), 100)]);
        await Assert.That(candidates.Count).IsEqualTo(1);
        await Assert.That(candidates[0].End).IsEqualTo(new JoinEnd(1, 0));
    }

    [Test]
    public async Task FindCandidates_FreeEndsAndToleranceBoundary()
    {
        var walls = new CadWallPlan[] { new(new(0, 0), new(1000, 0), 100), new(new(500, 10), new(500, 1000), 100) };
        await Assert.That(WallJoinPreflight.FindCandidates(walls).Count).IsEqualTo(1);
        await Assert.That(WallJoinPreflight.FindCandidates(walls, 9.99).Count).IsEqualTo(0);
        await Assert.That(WallJoinPreflight.FindCandidates([walls[0]]).Count).IsEqualTo(0);
    }

    private static readonly List<JoinCandidate> Candidates =
    [
        new(new(0, 1), new(1000, 0)), new(new(1, 0), new(1000, 0)),
        new(new(0, 0), new(0, 0)), new(new(1, 1), new(1000, 1000)),
        new(new(2, 0), new(1000, 0))
    ];

    [Test]
    public async Task DeriveSkips_UsesNamedWallsAndNearbySegments()
    {
        var failures = new PreflightFailure[] { new("cannot join", [
            new(10, 0, new(0, 0), new(1000, 0), null),
            new(11, 1, new(1000, 0), new(1000, 1000), null),
            new(12, null, new(990, -100), new(990, 100), null)]) };
        var skips = WallJoinPreflight.DeriveSkips(Candidates, new HashSet<JoinEnd>(), failures);
        await Assert.That(string.Join(",", skips.Select(skip => $"{skip.End.Wall}:{skip.End.End}"))).IsEqualTo("0:1,1:0");
        await Assert.That(skips[0].Reason).IsEqualTo("cannot join");
    }

    [Test]
    public async Task DeriveSkips_OtherNewWallsDoNotDropUnrelatedEnds()
    {
        var skips = WallJoinPreflight.DeriveSkips(Candidates, new HashSet<JoinEnd>(), [new("other walls", [
            new(20, 3, new(0, 0), new(1000, 0), null), new(21, 4, new(1000, 0), new(1000, 1000), null)])]);
        await Assert.That(skips.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeriveSkips_ExistingSegmentUsesTwiceTheTolerance()
    {
        var candidates = new JoinCandidate[] { new(new(0, 0), new(500, 20)), new(new(0, 1), new(500, 20.01)) };
        var skips = WallJoinPreflight.DeriveSkips(candidates, new HashSet<JoinEnd>(), [new("existing", [
            new(30, null, new(0, 0), new(1000, 0), null)])]);
        await Assert.That(skips.Count).IsEqualTo(1);
        await Assert.That(skips[0].End).IsEqualTo(new JoinEnd(0, 0));
    }

    [Test]
    public async Task DeriveSkips_FallbackExpandsBoxesByOneMetre()
    {
        var candidates = new JoinCandidate[] { new(new(0, 0), new(-1000, 1000)), new(new(0, 1), new(-1000.01, 1000)) };
        var skips = WallJoinPreflight.DeriveSkips(candidates, new HashSet<JoinEnd>(), [new("box", [
            new(30, null, null, null, new(0, 0, 100, 100))])]);
        await Assert.That(skips.Count).IsEqualTo(1);
        await Assert.That(skips[0].End).IsEqualTo(new JoinEnd(0, 0));
    }

    [Test]
    public async Task DeriveSkips_FallbackDropsNamedWallEnds()
    {
        var skips = WallJoinPreflight.DeriveSkips(Candidates, new HashSet<JoinEnd>(), [new("named", [new(10, 0, null, null, null)])]);
        await Assert.That(string.Join(",", skips.Select(skip => skip.End.End))).IsEqualTo("0,1");
    }

    [Test]
    public async Task DeriveSkips_ExcludesAlreadySkippedAndKeepsFirstReason()
    {
        var failures = new PreflightFailure[] {
            new("first", [new(10, 0, null, null, null)]), new("second", [new(10, 0, null, null, null)]) };
        var skips = WallJoinPreflight.DeriveSkips(Candidates, new HashSet<JoinEnd> { new(0, 0) }, failures);
        await Assert.That(skips.Count).IsEqualTo(1);
        await Assert.That(skips[0].Reason).IsEqualTo("first");
        await Assert.That(WallJoinPreflight.DeriveSkips(Candidates, new HashSet<JoinEnd>(), []).Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeriveSkips_FallbackRunsOnlyWhenPrimaryMakesNoProgress()
    {
        var skips = WallJoinPreflight.DeriveSkips(Candidates, new HashSet<JoinEnd>(), [
            new("segment", [new(30, null, new(990, -100), new(990, 100), null)]),
            new("box", [new(31, null, null, null, new(0, 0, 1000, 1000))])]);
        await Assert.That(skips.Count).IsEqualTo(3);
        await Assert.That(skips.All(skip => skip.Reason == "segment")).IsTrue();
    }

    [Test]
    public async Task BuildWarning_FormatsPluralSingularAndTruncatedIds()
    {
        await Assert.That(WallJoinPreflight.BuildWarning(2, [3, 7], true)).IsEqualTo(
            "2 wall ends were left unjoined because Revit could not keep or cut the joins there; existing walls near them: ids 3, 7.");
        await Assert.That(WallJoinPreflight.BuildWarning(1, [], true)).IsEqualTo(
            "1 wall end was left unjoined because Revit could not keep or cut the joins there.");
        var ids = Enumerable.Range(1, 21).Select(id => (long)id).ToList();
        await Assert.That(WallJoinPreflight.BuildWarning(2, ids, true)).IsEqualTo(
            "2 wall ends were left unjoined because Revit could not keep or cut the joins there; existing walls near them: ids " + string.Join(", ", ids.Take(20)) + ", ....");
        await Assert.That(WallJoinPreflight.BuildWarning(2, ids, false)).IsEqualTo(
            "Join preflight did not converge in 6 rounds; all walls were created without joins.");
    }

    [Test]
    public async Task ActionResult_SerializesJoinFieldsAndOmitsUnsetFields()
    {
        var serializer = new DataContractJsonSerializer(typeof(ActionResultData), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, new ActionResultData { JoinedEnds = 2, UnjoinedEnds = 1, UnjoinedReasons = new() { ["cannot join"] = 1 } });
        var json = Encoding.UTF8.GetString(stream.ToArray());
        await Assert.That(json).Contains("\"joinedEnds\":2");
        await Assert.That(json).Contains("\"unjoinedEnds\":1");
        await Assert.That(json).Contains("\"unjoinedReasons\":{\"cannot join\":1}");
        stream.SetLength(0);
        serializer.WriteObject(stream, new ActionResultData());
        await Assert.That(Encoding.UTF8.GetString(stream.ToArray())).DoesNotContain("joinedEnds");
        await Assert.That(Encoding.UTF8.GetString(stream.ToArray())).DoesNotContain("unjoinedReasons");
    }
}
