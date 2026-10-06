using Autodesk.Revit.DB;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal sealed class PreflightState
{
    public HashSet<long> InGroup { get; } = [];
    public HashSet<JoinEnd> SkippedEnds { get; } = [];
    public List<JoinCandidate> Candidates { get; set; } = [];
    public Dictionary<string, int> UnjoinedReasons { get; } = [];
    public List<long> NearbyExistingIds { get; } = [];
    public bool JoinConverged { get; set; } = true;
    public ActionMutations.WallPlan? WallPlan { get; set; }
}

internal static class ActionPreflight
{
    internal static PreflightState? Run(Document document, string command, ActionJobContract action, List<ElementId> ids)
    {
        if (command is "update-parameters" or "set-parameter")
        {
            using var groups = new FilteredElementCollector(document).OfClass(typeof(Group));
            if (groups.GetElementCount() == 0) return null;
            var state = new PreflightState();
            var matchedIds = command == "update-parameters"
                ? ActionMutations.MatchedIds(document, action).Select(RevitValueReader.GetId).ToList() : [action.ElementId];
            for (var round = 0; round < 4; round++)
            {
                var failures = new PreflightFailures();
                Probe(document, failures, () =>
                {
                    if (command == "update-parameters") ActionMutations.UpdateParameters(document, action, state.InGroup);
                    else ActionMutations.SetParameter(document, action);
                });
                var groupFailures = failures.Errors.Where(failure => failure.Guid == GroupFailurePolicy.FailureId)
                    .Select(failure => (IReadOnlyCollection<long>)failure.Ids).ToList();
                if (command == "set-parameter")
                {
                    if (groupFailures.Count > 0)
                        throw new InvalidOperationException($"Element {action.ElementId} belongs to a group and Revit allows changes to group members only in group edit mode. Nothing was changed.");
                    return state;
                }
                var blocked = GroupFailurePolicy.BlockedIds(matchedIds, groupFailures).Where(id => !state.InGroup.Contains(id)).ToList();
                if (blocked.Count == 0) return state;
                state.InGroup.UnionWith(blocked);
            }
            throw new InvalidOperationException("Revit still rejects changes to groups after 4 preflight rounds; narrow the filter to elements outside groups.");
        }
        if (GroupSkipPolicy.Supports(command))
        {
            using var groups = new FilteredElementCollector(document).OfClass(typeof(Group));
            if (groups.GetElementCount() == 0) return null;
            var state = new PreflightState();
            var requested = ids.Select(RevitValueReader.GetId).ToList();
            var converged = false;
            for (var round = 0; round < 4; round++)
            {
                var remaining = ids.Where(id => !state.InGroup.Contains(RevitValueReader.GetId(id))).ToList();
                var failures = new PreflightFailures();
                Probe(document, failures, () => ApplyGroupAction(document, command, action, remaining));
                var groupFailures = failures.Errors.Where(failure => failure.Guid == GroupFailurePolicy.FailureId)
                    .Select(failure => (IReadOnlyCollection<long>)failure.Ids).ToList();
                var blocked = GroupFailurePolicy.BlockedIds(remaining.Select(RevitValueReader.GetId), groupFailures, id =>
                {
                    var groupId = document.GetElement(ActionCommandExecutor.CreateId(id))?.GroupId;
                    return groupId is not null && groupId != ElementId.InvalidElementId ? RevitValueReader.GetId(groupId) : (long?)null;
                }).Where(id => !state.InGroup.Contains(id)).ToList();
                if (blocked.Count == 0)
                {
                    converged = true;
                    break;
                }
                state.InGroup.UnionWith(blocked);
                if (GroupSkipPolicy.Decide(requested.Count, state.InGroup.Count) == GroupSkipPolicy.GroupSkipDecision.RefuseAll)
                    break;
            }
            if (GroupSkipPolicy.Decide(requested.Count, state.InGroup.Count) == GroupSkipPolicy.GroupSkipDecision.RefuseAll)
                throw new InvalidOperationException(GroupSkipPolicy.RefusalMessage(requested.Count));
            if (!converged)
                throw new InvalidOperationException("Revit still rejects changes to groups after 4 preflight rounds; narrow the element list to elements outside groups.");
            return state;
        }
        if (command != "walls-from-cad" || !action.Join) return null;
        var plan = ActionMutations.PlanWalls(document, action);
        if (plan.NoBasicTypes) return null;
        var joinState = new PreflightState { WallPlan = plan, Candidates = WallJoinPreflight.FindCandidates(plan.Geometry.Walls) };
        if (joinState.Candidates.Count == 0) return joinState;
        for (var round = 1; round <= WallJoinPreflight.MaxRounds; round++)
        {
            var failures = new PreflightFailures();
            var newWalls = new Dictionary<long, int>();
            var status = Probe(document, failures, () =>
            {
                var created = new List<Wall>();
                foreach (var wall in plan.Planned)
                {
                    var element = Wall.Create(document, Line.CreateBound(wall.Start, wall.End), wall.Type.Id,
                        plan.Level.Id, UnitUtils.ConvertToInternalUnits(action.HeightMm, UnitTypeId.Millimeters), 0, false, false);
                    WallUtils.DisallowWallJoinAtEnd(element, 0);
                    WallUtils.DisallowWallJoinAtEnd(element, 1);
                    newWalls.Add(RevitValueReader.GetId(element.Id), created.Count);
                    created.Add(element);
                }
                foreach (var candidate in joinState.Candidates)
                    if (!joinState.SkippedEnds.Contains(candidate.End))
                        WallUtils.AllowWallJoinAtEnd(created[candidate.End.Wall], candidate.End.End);
            });
            if (status == TransactionStatus.Committed)
            {
                joinState.NearbyExistingIds.Sort();
                return joinState;
            }
            var mapped = new List<PreflightFailure>();
            foreach (var failure in failures.Errors)
            {
                var elements = new List<PreflightElement>();
                foreach (var id in failure.Ids)
                {
                    if (newWalls.TryGetValue(id, out var index))
                    {
                        var wall = plan.Geometry.Walls[index];
                        elements.Add(new PreflightElement(id, index, wall.Start, wall.End, null));
                        continue;
                    }
                    if (!joinState.NearbyExistingIds.Contains(id)) joinState.NearbyExistingIds.Add(id);
                    var element = document.GetElement(ActionCommandExecutor.CreateId(id));
                    if (element?.Location is LocationCurve { Curve: Line line })
                        elements.Add(new PreflightElement(id, null, Point(line.GetEndPoint(0)), Point(line.GetEndPoint(1)), null));
                    else if (element?.get_BoundingBox(null) is BoundingBoxXYZ box)
                        elements.Add(new PreflightElement(id, null, null, null,
                            new PreflightBox(box.Min.X.ToMillimeters(), box.Min.Y.ToMillimeters(), box.Max.X.ToMillimeters(), box.Max.Y.ToMillimeters())));
                }
                mapped.Add(new PreflightFailure(failure.Description, elements));
            }
            var skips = WallJoinPreflight.DeriveSkips(joinState.Candidates, joinState.SkippedEnds, mapped);
            foreach (var skip in skips) AddSkip(joinState, skip.End, skip.Reason);
            if (skips.Count == 0 || round == WallJoinPreflight.MaxRounds)
            {
                joinState.JoinConverged = false;
                foreach (var candidate in joinState.Candidates)
                    AddSkip(joinState, candidate.End, "join preflight did not converge");
                break;
            }
        }
        joinState.NearbyExistingIds.Sort();
        return joinState;
    }

    private static void ApplyGroupAction(Document document, string command, ActionJobContract action, List<ElementId> ids)
    {
        var originalIds = action.ElementIds;
        try
        {
            action.ElementIds = ids.Select(RevitValueReader.GetId).ToList();
            _ = command switch
            {
                "move" => ActionMutations.Move(document, action, ids),
                "rotate" => ActionMutations.Rotate(document, action, ids),
                "copy" => ActionMutations.Copy(document, action, ids),
                "mirror" => ActionMutations.Mirror(document, action, ids),
                "change-type" => ActionMutations.ChangeType(document, action, ids),
                _ => throw new ArgumentException($"Unsupported group preflight command '{command}'.")
            };
        }
        finally
        {
            action.ElementIds = originalIds;
        }
    }

    private static CadPlanPoint Point(XYZ point) => new(point.X.ToMillimeters(), point.Y.ToMillimeters());

    private static void AddSkip(PreflightState state, JoinEnd end, string reason)
    {
        if (!state.SkippedEnds.Add(end)) return;
        state.UnjoinedReasons.TryGetValue(reason, out var count);
        state.UnjoinedReasons[reason] = count + 1;
    }

    private static TransactionStatus Probe(Document document, PreflightFailures failures, Action mutation)
    {
        using var group = new TransactionGroup(document, "preflight");
        using var transaction = new Transaction(document, "preflight");
        try
        {
            if (group.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Could not start the preflight transaction group.");
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Could not start the preflight transaction.");
            transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
                .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
            mutation();
            document.Regenerate();
            return transaction.Commit();
        }
        finally
        {
            try
            {
                if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            }
            finally
            {
                if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            }
        }
    }

    private sealed class PreflightFailures : IFailuresPreprocessor
    {
        public List<(string Description, Guid Guid, List<long> Ids)> Errors { get; } = [];

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                    failuresAccessor.DeleteWarning(failure);
                else if (failure.GetSeverity() == FailureSeverity.Error)
                    Errors.Add((failure.GetDescriptionText(), failure.GetFailureDefinitionId().Guid,
                        failure.GetFailingElementIds().Select(RevitValueReader.GetId).ToList()));
            }
            return Errors.Count > 0 ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
