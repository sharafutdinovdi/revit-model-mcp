using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Control;

public static class ActionJobParser
{
    public static void ValidateFamilyMode(ActionJobContract action, bool isFamilyDocument)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        if (isFamilyDocument && action.Families is not null)
            throw new ArgumentException("families must be absent in family mode.");
        if (!isFamilyDocument && action.Families is null)
            throw new ArgumentException("families is required in project mode.");
    }

    public static bool IsAction(string command) => command is
        "select" or "show" or "isolate" or "move" or "place-family" or "create-wall" or "set-parameter" or "delete" or "batch" or "edit-families";

    public static ControlJobParseResult Parse(string command, ControlJobContract job)
    {
        try
        {
            var action = new ActionJobContract
            {
                DryRun = job.DryRun ?? false,
                ElementIds = (job.ElementIds ?? []).Distinct().ToList(),
                Select = job.Select ?? true,
                Reset = job.Reset ?? false,
                DxMm = job.DxMm ?? 0,
                DyMm = job.DyMm ?? 0,
                DzMm = job.DzMm ?? 0,
                Family = job.Family,
                TypeName = job.TypeName,
                Level = job.Level,
                XMm = job.XMm ?? 0,
                YMm = job.YMm ?? 0,
                RotationDeg = job.RotationDeg ?? 0,
                StartMm = job.StartMm ?? [],
                EndMm = job.EndMm ?? [],
                WallType = job.WallType,
                HeightMm = job.HeightMm ?? 3000,
                ElementId = job.ActionElementId ?? 0,
                Parameter = job.Parameter,
                Value = job.Value,
                Families = job.Families,
                Operations = job.Operations ?? [],
                OverwriteParameterValues = job.OverwriteParameterValues ?? false,
                StopOnError = job.StopOnError ?? true
            };
            if (command == "batch")
            {
                Require(job.Steps is { Count: > 0 and <= 50 }, "batch requires 1 to 50 steps.");
                foreach (var step in job.Steps!)
                {
                    var stepCommand = step?.Command ?? string.Empty;
                    Require(IsAction(stepCommand) && stepCommand is not ("show" or "batch" or "edit-families" or "family-audit"),
                        "Batch steps must be move, place-family, create-wall, set-parameter, delete, select or isolate.");
                    var parsed = Parse(stepCommand, step!);
                    Require(parsed.Error is null, $"Step {action.Steps.Count}: {parsed.Error}");
                    action.Steps.Add(parsed);
                }
            }
            if (command is "edit-families" or "family-audit")
            {
                Require(job.Families is null || job.Families.Count is > 0 and <= 200,
                    "families must contain 1 to 200 names.");
                Require(job.Families is null || job.Families.All(name => !string.IsNullOrWhiteSpace(name)),
                    "Family names must not be blank.");
                Require(job.Families is null || !job.Families.Contains("*") || job.Families.Count == 1,
                    "The '*' family selector must be alone.");
            }
            if (command == "edit-families")
            {
                Require(action.Operations.Count > 0, "operations must not be empty.");
                foreach (var operation in action.Operations)
                {
                    if (operation is null) throw new ArgumentException("operations must not contain null.");
                    Require(operation.Op is "add_shared_parameters" or "remove_parameters" or "purge" or "set_shared",
                        $"Unknown family operation: {operation.Op}.");
                    if (operation.Op == "add_shared_parameters")
                    {
                        if (operation.Parameters is not { Count: > 0 })
                            throw new ArgumentException("add_shared_parameters requires parameters.");
                        Require(operation.Parameters.All(parameter => !string.IsNullOrWhiteSpace(parameter.Name) && !string.IsNullOrWhiteSpace(parameter.Group)),
                            "Shared parameter name and group are required.");
                        Require(operation.Parameters.All(parameter => parameter.Guid is null || Guid.TryParse(parameter.Guid, out _)),
                            "Shared parameter GUID is invalid.");
                    }
                    if (operation.Op == "remove_parameters")
                        Require(operation.Names is { Count: > 0 } && operation.Names.All(name => !string.IsNullOrWhiteSpace(name)),
                            "remove_parameters requires non-empty names.");
                    if (operation.Op == "set_shared")
                        Require(operation.Shared.HasValue, "set_shared requires shared.");
                }
            }
            if (command is "select" or "show" or "isolate" or "move" or "delete")
            {
                Require(job.ElementIds is not null, "elementIds is required.");
                Require(action.ElementIds.All(elementId => elementId > 0), "Element IDs must be positive.");
                Require(action.ElementIds.Count > 0 || command == "select" || command == "isolate" && action.Reset,
                    "elementIds must not be empty.");
            }
            if (command == "move")
            {
                Require(job.DxMm.HasValue && job.DyMm.HasValue, "dxMm and dyMm are required.");
                Require(Finite(action.DxMm, action.DyMm, action.DzMm), "Move offsets must be finite millimetres.");
            }
            if (command == "place-family")
            {
                Require(!string.IsNullOrWhiteSpace(action.Family), "family is required.");
                var familyParts = action.Family!.Split([':'], 2);
                action.Family = familyParts[0].Trim();
                Require(action.Family.Length > 0, "family is required.");
                if (familyParts.Length == 2)
                {
                    var embeddedType = familyParts[1].Trim();
                    Require(embeddedType.Length > 0, "The type in Family: Type must not be blank.");
                    Require(action.TypeName is null || string.Equals(action.TypeName.Trim(), embeddedType, StringComparison.OrdinalIgnoreCase),
                        "typeName conflicts with the type in Family: Type.");
                    action.TypeName = embeddedType;
                }
                else action.TypeName = action.TypeName?.Trim();
                Require(job.XMm.HasValue && job.YMm.HasValue, "xMm and yMm are required.");
                Require(Finite(action.XMm, action.YMm, action.RotationDeg), "Placement coordinates and rotation must be finite.");
                Require(action.TypeName is null || !string.IsNullOrWhiteSpace(action.TypeName), "typeName must not be blank.");
            }
            if (command is "place-family" or "create-wall")
                Require(!string.IsNullOrWhiteSpace(action.Level), "level is required.");
            if (command == "create-wall")
            {
                Require(action.StartMm.Count == 2 && action.EndMm.Count == 2, "startMm and endMm must each contain two coordinates.");
                Require(Finite(action.StartMm.Concat(action.EndMm).ToArray()), "Wall coordinates must be finite millimetres.");
                Require(!action.StartMm.SequenceEqual(action.EndMm), "Wall endpoints must differ.");
                Require(Finite(action.HeightMm) && action.HeightMm > 0, "heightMm must be finite and positive.");
                Require(action.WallType is null || !string.IsNullOrWhiteSpace(action.WallType), "wallType must not be blank.");
            }
            if (command == "set-parameter")
            {
                Require(action.ElementId > 0, "elementId must be positive.");
                Require(!string.IsNullOrWhiteSpace(action.Parameter), "parameter is required.");
                Require(action.Value is not null, "value is required (an empty string is allowed).");
            }
            var result = ControlJobParseResult.Create(ControlJobKind.Action, command);
            result.Action = action;
            return result;
        }
        catch (ArgumentException exception)
        {
            return ControlJobParseResult.Invalid(command, exception.Message);
        }
    }

    public static List<string> ClosestFamilyNames(string requested, IEnumerable<string> candidates) =>
        candidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new
            {
                Name = candidate,
                Contains = candidate.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0,
                Similarity = 1.0 - (double)NameDistance(requested, candidate) / Math.Max(1, Math.Max(requested.Length, candidate.Length))
            })
            .Where(candidate => candidate.Contains || candidate.Similarity >= 0.5)
            .OrderByDescending(candidate => candidate.Contains)
            .ThenByDescending(candidate => candidate.Similarity)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5).Select(candidate => candidate.Name).ToList();

    private static int NameDistance(string requested, string candidate)
    {
        var previous = Enumerable.Range(0, candidate.Length + 1).ToArray();
        for (var requestedIndex = 1; requestedIndex <= requested.Length; requestedIndex++)
        {
            var current = new int[candidate.Length + 1];
            current[0] = requestedIndex;
            for (var candidateIndex = 1; candidateIndex <= candidate.Length; candidateIndex++)
            {
                var cost = char.ToUpperInvariant(requested[requestedIndex - 1]) == char.ToUpperInvariant(candidate[candidateIndex - 1]) ? 0 : 1;
                current[candidateIndex] = Math.Min(Math.Min(current[candidateIndex - 1] + 1, previous[candidateIndex] + 1), previous[candidateIndex - 1] + cost);
            }
            previous = current;
        }
        return previous[candidate.Length];
    }

    private static bool Finite(params double[] values) => values.All(value => !double.IsNaN(value) && !double.IsInfinity(value));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}

public sealed class ActionJobContract
{
    public bool DryRun { get; set; }
    public List<ControlJobParseResult> Steps { get; set; } = [];
    public List<long> ElementIds { get; set; } = [];
    public bool Select { get; set; }
    public bool Reset { get; set; }
    public double DxMm { get; set; }
    public double DyMm { get; set; }
    public double DzMm { get; set; }
    public string? Family { get; set; }
    public string? TypeName { get; set; }
    public double XMm { get; set; }
    public double YMm { get; set; }
    public string? Level { get; set; }
    public double RotationDeg { get; set; }
    public List<double> StartMm { get; set; } = [];
    public List<double> EndMm { get; set; } = [];
    public string? WallType { get; set; }
    public double HeightMm { get; set; }
    public long ElementId { get; set; }
    public string? Parameter { get; set; }
    public string? Value { get; set; }
    public List<string>? Families { get; set; }
    public List<FamilyEditOperationContract> Operations { get; set; } = [];
    public bool OverwriteParameterValues { get; set; }
    public bool StopOnError { get; set; }
}

public sealed partial class ControlJobContract
{
    [DataMember(Name = "dryRun")] public bool? DryRun { get; set; }
    [DataMember(Name = "steps")] public List<ControlJobContract>? Steps { get; set; }
    [DataMember(Name = "elementIds")] public List<long>? ElementIds { get; set; }
    [DataMember(Name = "select")] public bool? Select { get; set; }
    [DataMember(Name = "reset")] public bool? Reset { get; set; }
    [DataMember(Name = "dxMm")] public double? DxMm { get; set; }
    [DataMember(Name = "dyMm")] public double? DyMm { get; set; }
    [DataMember(Name = "dzMm")] public double? DzMm { get; set; }
    [DataMember(Name = "typeName")] public string? TypeName { get; set; }
    [DataMember(Name = "xMm")] public double? XMm { get; set; }
    [DataMember(Name = "yMm")] public double? YMm { get; set; }
    [DataMember(Name = "rotationDeg")] public double? RotationDeg { get; set; }
    [DataMember(Name = "startMm")] public List<double>? StartMm { get; set; }
    [DataMember(Name = "endMm")] public List<double>? EndMm { get; set; }
    [DataMember(Name = "wallType")] public string? WallType { get; set; }
    [DataMember(Name = "heightMm")] public double? HeightMm { get; set; }
    [DataMember(Name = "elementId")] public long? ActionElementId { get; set; }
    [DataMember(Name = "parameter")] public string? Parameter { get; set; }
    [DataMember(Name = "value")] public string? Value { get; set; }
    [DataMember(Name = "families")] public List<string>? Families { get; set; }
    [DataMember(Name = "operations")] public List<FamilyEditOperationContract>? Operations { get; set; }
    [DataMember(Name = "overwriteParameterValues")] public bool? OverwriteParameterValues { get; set; }
    [DataMember(Name = "stopOnError")] public bool? StopOnError { get; set; }
}

[DataContract]
public sealed class FamilyEditOperationContract
{
    [DataMember(Name = "op")] public string? Op { get; set; }
    [DataMember(Name = "parameters")] public List<SharedParameterSpec>? Parameters { get; set; }
    [DataMember(Name = "replaceFamilyParameter")] public bool ReplaceFamilyParameter { get; set; }
    [DataMember(Name = "sharedParameterFile")] public string? SharedParameterFile { get; set; }
    [DataMember(Name = "names")] public List<string>? Names { get; set; }
    [DataMember(Name = "includeShared")] public bool IncludeShared { get; set; }
    [DataMember(Name = "shared")] public bool? Shared { get; set; }
}

[DataContract]
public sealed class SharedParameterSpec
{
    private bool? _instance;

    [DataMember(Name = "name")] public string? Name { get; set; }
    [DataMember(Name = "guid")] public string? Guid { get; set; }
    [DataMember(Name = "group")] public string? Group { get; set; }
    [DataMember(Name = "instance")] public bool Instance { get => _instance ?? true; set => _instance = value; }
}

[DataContract]
public sealed class ActionResultData
{
    [DataMember(Name = "dryRun", EmitDefaultValue = false)] public bool? DryRun { get; set; }
    [DataMember(Name = "rolledBack", EmitDefaultValue = false)] public bool? RolledBack { get; set; }
    [DataMember(Name = "verification", EmitDefaultValue = false)] public ActionVerification? Verification { get; set; }
    [DataMember(Name = "steps", EmitDefaultValue = false)] public List<BatchStepResult>? Steps { get; set; }
    [DataMember(Name = "undoName", EmitDefaultValue = false)] public string? UndoName { get; set; }
    [DataMember(Name = "committed", EmitDefaultValue = false)] public bool? Committed { get; set; }
    [DataMember(Name = "failedStep")] public int? FailedStep { get; set; }

    [DataMember(Name = "count", EmitDefaultValue = false)] public int? Count { get; set; }
    [DataMember(Name = "id", EmitDefaultValue = false)] public long? Id { get; set; }
    [DataMember(Name = "category", EmitDefaultValue = false)] public string? Category { get; set; }
    [DataMember(Name = "level", EmitDefaultValue = false)] public string? Level { get; set; }
    [DataMember(Name = "lengthMm", EmitDefaultValue = false)] public double? LengthMm { get; set; }
    [DataMember(Name = "oldValue", EmitDefaultValue = false)] public string? OldValue { get; set; }
    [DataMember(Name = "newValue", EmitDefaultValue = false)] public string? NewValue { get; set; }
    [DataMember(Name = "parameterScope", EmitDefaultValue = false)] public string? ParameterScope { get; set; }
    [DataMember(Name = "closestFamilies", EmitDefaultValue = false)] public List<string>? ClosestFamilies { get; set; }
}
