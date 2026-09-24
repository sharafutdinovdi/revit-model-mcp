using System.Runtime.Serialization;
using RevitModelMcp.Core.Export;
using RevitModelMcp.Core.Models;

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
        "select" or "show" or "isolate" or "move" or "place-family" or "create-wall" or "set-parameter" or "delete" or "batch" or "export-nwc" or "edit-families" or "align-link-datums" or "undo-last";

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
                Nwc = new NwcExportJob
                {
                    Path = job.Path ?? string.Empty, Scope = job.Scope ?? "model", View = job.View,
                    Coordinates = job.Coordinates ?? "shared", Parameters = job.NwcParameters ?? "all",
                    ExportElementIds = job.ExportElementIds ?? true,
                    ConvertElementProperties = job.ConvertElementProperties ?? false,
                    ExportParts = job.ExportParts ?? false,
                    ExportRoomAsAttribute = job.ExportRoomAsAttribute ?? true,
                    ExportRoomGeometry = job.ExportRoomGeometry ?? true,
                    ConvertLights = job.ConvertLights ?? false,
                    ConvertLinkedCadFormats = job.ConvertLinkedCadFormats ?? true,
                    ExportLinks = job.ExportLinks ?? false, ExportUrls = job.ExportUrls ?? true,
                    DivideFileIntoLevels = job.DivideFileIntoLevels ?? true,
                    FindMissingMaterials = job.FindMissingMaterials ?? true,
                    FacetingFactor = job.FacetingFactor ?? 1.0, Overwrite = job.Overwrite ?? false
                },
                Families = job.Families,
                Operations = job.Operations ?? [],
                OverwriteParameterValues = job.OverwriteParameterValues ?? false,
                StopOnError = job.StopOnError ?? true
            };
            if (command == "align-link-datums")
                action.DatumOptions = ParseDatumOptions(job);
            if (command == "batch")
            {
                Require(job.Steps is { Count: > 0 and <= 50 }, "batch requires 1 to 50 steps.");
                foreach (var step in job.Steps!)
                {
                    var stepCommand = step?.Command ?? string.Empty;
                    Require(IsAction(stepCommand) && stepCommand is not ("show" or "batch" or "export-nwc" or "edit-families" or "family-audit" or "align-link-datums" or "undo-last"),
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
            if (command == "export-nwc")
            {
                var export = action.Nwc;
                NwcPathValidator.Validate(export.Path);
                Require(export.Scope is "model" or "view" or "selection", "scope must be model, view or selection.");
                Require(export.Coordinates is "shared" or "internal", "coordinates must be shared or internal.");
                Require(export.Parameters is "all" or "elements" or "none", "parameters must be all, elements or none.");
                Require(Finite(export.FacetingFactor) && export.FacetingFactor is > 0 and <= 100,
                    "facetingFactor must be greater than 0 and at most 100.");
                Require(export.Scope != "view" || !string.IsNullOrWhiteSpace(export.View), "view is required for scope=view.");
                Require(export.Scope != "selection" || action.ElementIds.Count > 0, "elementIds must be non-empty for scope=selection.");
                Require(export.Scope != "selection" || action.ElementIds.All(id => id > 0), "Element IDs must be positive.");
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

    public static LinkDatumJobOptions ParseDatumOptions(ControlJobContract job)
    {
        Require(!string.IsNullOrWhiteSpace(job.Link), "link is required.");
        var kinds = job.Kinds ?? ["grids", "levels"];
        Require(kinds.Count > 0 && kinds.All(kind => kind is "grids" or "levels") &&
                kinds.Distinct().Count() == kinds.Count, "kinds must contain grids or levels without duplicates.");
        var tolerance = job.ToleranceMm ?? 0.5;
        var offset = job.LevelOffsetMm ?? 0;
        Require(Finite(tolerance, offset) && tolerance > 0, "toleranceMm must be finite and positive; levelOffsetMm must be finite.");
        Require(job.NameMap is null || job.NameMap.All(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)),
            "nameMap names must not be blank.");
        return new LinkDatumJobOptions
        {
            Link = job.Link!.Trim(),
            Kinds = kinds,
            NameMap = job.NameMap ?? [],
            Prefix = job.Prefix ?? "",
            Suffix = job.Suffix ?? "",
            LevelOffsetMm = offset,
            ReuseMatching = job.ReuseMatching ?? true,
            ToleranceMm = tolerance,
            CreateMissing = job.CreateMissing ?? true,
            LevelType = job.LevelType,
            GridType = job.GridType,
            IncludePinned = job.IncludePinned ?? false,
            CreatePlanViews = job.CreatePlanViews ?? false,
            PlanViewType = job.PlanViewType
        };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}

public sealed class ActionJobContract
{
    public NwcExportJob Nwc { get; set; } = new();
    public LinkDatumJobOptions? DatumOptions { get; set; }
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

public sealed class NwcExportJob
{
    public string Path { get; set; } = string.Empty;
    public string Scope { get; set; } = "model";
    public string? View { get; set; }
    public string Coordinates { get; set; } = "shared";
    public string Parameters { get; set; } = "all";
    public bool ExportElementIds { get; set; }
    public bool ConvertElementProperties { get; set; }
    public bool ExportParts { get; set; }
    public bool ExportRoomAsAttribute { get; set; }
    public bool ExportRoomGeometry { get; set; }
    public bool ConvertLights { get; set; }
    public bool ConvertLinkedCadFormats { get; set; }
    public bool ExportLinks { get; set; }
    public bool ExportUrls { get; set; }
    public bool DivideFileIntoLevels { get; set; }
    public bool FindMissingMaterials { get; set; }
    public double FacetingFactor { get; set; }
    public bool Overwrite { get; set; }
}

public sealed class LinkDatumJobOptions
{
    public string Link { get; set; } = "";
    public List<string> Kinds { get; set; } = ["grids", "levels"];
    public Dictionary<string, string> NameMap { get; set; } = [];
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
    public double LevelOffsetMm { get; set; }
    public bool ReuseMatching { get; set; } = true;
    public double ToleranceMm { get; set; } = 0.5;
    public bool CreateMissing { get; set; } = true;
    public string? LevelType { get; set; }
    public string? GridType { get; set; }
    public bool IncludePinned { get; set; }
    public bool CreatePlanViews { get; set; }
    public string? PlanViewType { get; set; }
}

public sealed partial class ControlJobContract
{
    [DataMember(Name = "path")] public string? Path { get; set; }
    [DataMember(Name = "scope")] public string? Scope { get; set; }
    [DataMember(Name = "coordinates")] public string? Coordinates { get; set; }
    [DataMember(Name = "exportElementIds")] public bool? ExportElementIds { get; set; }
    [DataMember(Name = "convertElementProperties")] public bool? ConvertElementProperties { get; set; }
    [DataMember(Name = "exportParts")] public bool? ExportParts { get; set; }
    [DataMember(Name = "exportRoomAsAttribute")] public bool? ExportRoomAsAttribute { get; set; }
    [DataMember(Name = "exportRoomGeometry")] public bool? ExportRoomGeometry { get; set; }
    [DataMember(Name = "convertLights")] public bool? ConvertLights { get; set; }
    [DataMember(Name = "convertLinkedCadFormats")] public bool? ConvertLinkedCadFormats { get; set; }
    [DataMember(Name = "exportLinks")] public bool? ExportLinks { get; set; }
    [DataMember(Name = "exportUrls")] public bool? ExportUrls { get; set; }
    [DataMember(Name = "divideFileIntoLevels")] public bool? DivideFileIntoLevels { get; set; }
    [DataMember(Name = "findMissingMaterials")] public bool? FindMissingMaterials { get; set; }
    [DataMember(Name = "facetingFactor")] public double? FacetingFactor { get; set; }
    [DataMember(Name = "overwrite")] public bool? Overwrite { get; set; }
    [DataMember(Name = "nwcParameters")] public string? NwcParameters { get; set; }
    [DataMember(Name = "link")] public string? Link { get; set; }
    [DataMember(Name = "kinds")] public List<string>? Kinds { get; set; }
    [DataMember(Name = "nameMap")] public Dictionary<string, string>? NameMap { get; set; }
    [DataMember(Name = "prefix")] public string? Prefix { get; set; }
    [DataMember(Name = "suffix")] public string? Suffix { get; set; }
    [DataMember(Name = "levelOffsetMm")] public double? LevelOffsetMm { get; set; }
    [DataMember(Name = "reuseMatching")] public bool? ReuseMatching { get; set; }
    [DataMember(Name = "toleranceMm")] public double? ToleranceMm { get; set; }
    [DataMember(Name = "createMissing")] public bool? CreateMissing { get; set; }
    [DataMember(Name = "levelType")] public string? LevelType { get; set; }
    [DataMember(Name = "gridType")] public string? GridType { get; set; }
    [DataMember(Name = "includePinned")] public bool? IncludePinned { get; set; }
    [DataMember(Name = "createPlanViews")] public bool? CreatePlanViews { get; set; }
    [DataMember(Name = "planViewType")] public string? PlanViewType { get; set; }
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
    [DataMember(Name = "path", EmitDefaultValue = false)] public string? Path { get; set; }
    [DataMember(Name = "bytes", EmitDefaultValue = false)] public long? Bytes { get; set; }
    [DataMember(Name = "sha256", EmitDefaultValue = false)] public string? Sha256 { get; set; }
    [DataMember(Name = "elapsedMs", EmitDefaultValue = false)] public long? ElapsedMs { get; set; }
    [DataMember(Name = "scope", EmitDefaultValue = false)] public string? Scope { get; set; }
    [DataMember(Name = "view")] public NwcViewResult? View { get; set; }
    [DataMember(Name = "elementCount", EmitDefaultValue = false)] public int? ElementCount { get; set; }
    [DataMember(Name = "options", EmitDefaultValue = false)] public NwcOptionsResult? Options { get; set; }
    [DataMember(Name = "overwritten", EmitDefaultValue = false)] public bool? Overwritten { get; set; }
    [DataMember(Name = "exporterAvailable", EmitDefaultValue = false)] public bool? ExporterAvailable { get; set; }
    [DataMember(Name = "pathChecks", EmitDefaultValue = false)] public NwcPathChecks? PathChecks { get; set; }
    [DataMember(Name = "link", EmitDefaultValue = false)] public RevitModelMcp.Core.Models.LinkDatumLink? Link { get; set; }
    [DataMember(Name = "toleranceMm", EmitDefaultValue = false)] public double? ToleranceMm { get; set; }
    [DataMember(Name = "levelOffsetMm", EmitDefaultValue = false)] public double? LevelOffsetMm { get; set; }
    [DataMember(Name = "items", EmitDefaultValue = false)] public List<RevitModelMcp.Core.Models.LinkDatumItem>? Items { get; set; }
    [DataMember(Name = "datumSummary", EmitDefaultValue = false)] public RevitModelMcp.Core.Models.LinkDatumSummary? DatumSummary { get; set; }
    [DataMember(Name = "warning", EmitDefaultValue = false)] public string? Warning { get; set; }
    [DataMember(Name = "dryRun", EmitDefaultValue = false)] public bool? DryRun { get; set; }
    [DataMember(Name = "rolledBack", EmitDefaultValue = false)] public bool? RolledBack { get; set; }
    [DataMember(Name = "verification", EmitDefaultValue = false)] public ActionVerification? Verification { get; set; }
    [DataMember(Name = "steps", EmitDefaultValue = false)] public List<BatchStepResult>? Steps { get; set; }
    [DataMember(Name = "undoName", EmitDefaultValue = false)] public string? UndoName { get; set; }
    [DataMember(Name = "committed", EmitDefaultValue = false)] public bool? Committed { get; set; }
    [DataMember(Name = "failedStep")] public int? FailedStep { get; set; }
    [DataMember(Name = "summary", EmitDefaultValue = false)] public string? Summary { get; set; }

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
