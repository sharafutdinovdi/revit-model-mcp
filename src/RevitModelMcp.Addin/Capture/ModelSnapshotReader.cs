using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal static class ModelSnapshotReader
{
    public static ModelSnapshotData Read(Document document, IReadOnlyList<ModelSnapshotParameterRule> rules)
    {
        if (document.IsFamilyDocument) throw new ArgumentException("A project document is required.");
        var diagnostics = new SkippedReadDiagnostics();
        var result = new ModelSnapshotData
        {
            CollectedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            Source = { Path = string.IsNullOrWhiteSpace(document.PathName) ? null : document.PathName,
                Kind = Kind(document.PathName), RuntimeYear = ParseYear(document.Application.VersionNumber) },
            Passport = { Title = document.Title, IsWorkshared = document.IsWorkshared }
        };
        try
        {
            ReadSource(document, result, diagnostics);
            result.Passport.Worksets = ReadCommandReader.ReadUserWorksets(document).Select(workset => new ModelSnapshotWorkset
            {
                Name = workset.Name,
                Owner = Try("workset owner", () => workset.Owner, diagnostics),
                IsOpen = workset.IsOpen
            }).ToList();
            if (result.Passport.Worksets.Any(workset => !workset.IsOpen))
                diagnostics.Add("closed user worksets", new InvalidOperationException(
                    "Element, family instance, warning attribution, and parameter-fill results may be incomplete."));
        }
        catch (Exception exception) { diagnostics.Add("worksets", exception); }

        var previous = SkippedReadDiagnostics.Current;
        SkippedReadDiagnostics.Current = diagnostics;
        try
        {
            var counts = ModelHealthReader.ReadCounts(document, ["elements", "views", "sheets", "linksRvt"]);
            foreach (var key in new[] { "elements", "views", "sheets" })
                result.Passport.Counts[key] = counts.GetValueOrDefault(key);
            result.Passport.Counts["links"] = counts.GetValueOrDefault("linksRvt");
        }
        finally { SkippedReadDiagnostics.Current = previous; }

        var warnings = Try("warnings", document.GetWarnings, diagnostics);
        if (warnings is not null)
            result.Warnings = Try("warning groups", () => ModelWarningReader.ReadSnapshot(warnings), diagnostics) ?? new();
        result.Families = Try("families", () => ReadFamilies(document, warnings, diagnostics), diagnostics) ?? new();
        result.Passport.Counts["families"] = result.Families.Total;
        result.Passport.Counts["familyTypes"] = result.Families.Items.Sum(family => family.TypeCount);
        foreach (var rule in rules)
        {
            var fill = Try($"parameter fill {rule.Category}/{rule.Parameter}", () => ParameterFillReader.Read(document,
                new ControlJobContract { Categories = [rule.Category], Parameters = [rule.Parameter], IncludeTypes = true, SampleLimit = 20 }), diagnostics);
            if (fill is null) continue;
            var item = fill.Parameters[0];
            result.ParameterFill.Rows.Add(new ModelSnapshotParameterRow
            {
                Category = rule.Category,
                Parameter = rule.Parameter,
                Total = item.Elements,
                Filled = item.Filled,
                Empty = item.Empty,
                Missing = item.Missing,
                SampleEmptyIds = item.EmptySampleIds
            });
        }
        result.Skipped = diagnostics.Items;
        result.SkippedCount = diagnostics.Count;
        return result;
    }

    private static void ReadSource(Document document, ModelSnapshotData result, SkippedReadDiagnostics diagnostics)
    {
        var path = document.PathName;
        if (document.IsWorkshared)
            result.Passport.CentralPath = Try("central path", () =>
                ModelPathUtils.ConvertModelPathToUserVisiblePath(document.GetWorksharingCentralModelPath()), diagnostics);
        var version = Try("document version", () => Document.GetDocumentVersion(document), diagnostics);
        if (version is not null)
        {
            result.Passport.NumberOfSaves = version.NumberOfSaves;
            result.Passport.VersionGuid = version.VersionGUID.ToString();
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            diagnostics.Add("source file", new FileNotFoundException("The document has no saved source path."));
            return;
        }
        if (result.Source.Kind == "rsn") return;
        var basic = Try("basic file info", () => BasicFileInfo.Extract(path), diagnostics);
        if (basic is not null)
        {
            using (basic)
            {
                result.Source.SavedInYear = ParseYear(basic.Format);
                result.Passport.BasicFileInfoUsername = Try("basic file info username", () => basic.Username, diagnostics);
                if (version is null)
                {
                    var fileVersion = Try("basic file version", basic.GetDocumentVersion, diagnostics);
                    if (fileVersion is not null)
                    {
                        result.Passport.NumberOfSaves = fileVersion.NumberOfSaves;
                        result.Passport.VersionGuid = fileVersion.VersionGUID.ToString();
                    }
                }
            }
        }
        result.Source.UpgradedInMemory = result.Source.SavedInYear is int saved &&
            result.Source.RuntimeYear is int runtime && saved < runtime;
        var file = Try("source file metadata", () => new FileInfo(path), diagnostics);
        if (file is null) return;
        if (!file.Exists)
        {
            diagnostics.Add("source file metadata", new FileNotFoundException("The source file is unavailable."));
            return;
        }
        result.Passport.FileLastWriteUtc = Try("fileLastWriteUtc", () =>
            file.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), diagnostics);
        result.Passport.FileSizeBytes = Try<long?>("fileSizeBytes", () => file.Length, diagnostics);
    }

    private static ModelSnapshotFamilies ReadFamilies(Document document, IList<FailureMessage>? warnings, SkippedReadDiagnostics diagnostics)
    {
        var families = new FilteredElementCollector(document).OfClass(typeof(Family)).Cast<Family>()
            .OrderBy(family => family.Name, StringComparer.OrdinalIgnoreCase).ThenBy(family => RevitValueReader.GetId(family.Id)).ToList();
        var counts = new Dictionary<long, int>();
        foreach (var instance in new FilteredElementCollector(document).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
        {
            var familyId = RevitValueReader.GetId(instance.Symbol.Family.Id);
            counts[familyId] = counts.GetValueOrDefault(familyId) + 1;
        }
        var warningsByFamily = new Dictionary<long, int>();
        if (warnings is not null)
            foreach (var warning in warnings)
            {
                var affected = new HashSet<long>();
                foreach (var id in warning.GetFailingElements().Concat(warning.GetAdditionalElements()))
                    if (Try("warning family attribution", () => document.GetElement(id), diagnostics) is FamilyInstance instance)
                        affected.Add(RevitValueReader.GetId(instance.Symbol.Family.Id));
                foreach (var familyId in affected)
                    warningsByFamily[familyId] = warningsByFamily.GetValueOrDefault(familyId) + 1;
            }
        var result = new ModelSnapshotFamilies { Total = families.Count };
        foreach (var family in families)
        {
            var id = RevitValueReader.GetId(family.Id);
            var item = new ModelSnapshotFamily
            {
                Name = family.Name,
                Category = family.FamilyCategory?.Name,
                IsInPlace = family.IsInPlace,
                IsEditable = family.IsEditable,
                TypeCount = Try<int?>("family type count", () => family.GetFamilySymbolIds().Count, diagnostics) ?? 0,
                InstanceCount = counts.GetValueOrDefault(id),
                WarningCount = warningsByFamily.GetValueOrDefault(id)
            };
            result.Items.Add(item);
            if (item.IsInPlace) result.InPlace++;
            if (!item.IsEditable) result.NonEditable++;
            if (item.WarningCount > 0) AddSignal(result, item, "in-warnings", $"{item.WarningCount} warning records.");
            if (item.TypeCount == 0) AddSignal(result, item, "no-types", "No family types.");
            if (item.InstanceCount == 0) AddSignal(result, item, "no-instances", "No placed instances.");
            if (!item.IsEditable) AddSignal(result, item, "not-editable", "Family is not editable.");
        }
        return result;
    }

    private static void AddSignal(ModelSnapshotFamilies result, ModelSnapshotFamily item, string signal, string detail) =>
        result.Signals.Add(new ModelSnapshotFamilySignal { Family = item.Name, Signal = signal, Detail = detail });

    private static T? Try<T>(string what, Func<T> read, SkippedReadDiagnostics diagnostics)
    {
        try { return read(); }
        catch (Exception exception) { diagnostics.Add(what, exception); return default; }
    }

    private static string Kind(string? path) => path?.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase) == true
        ? "rsn" : path?.StartsWith(@"\\", StringComparison.Ordinal) == true ? "unc" : "local";

    private static int? ParseYear(string? value)
    {
        var match = Regex.Match(value ?? string.Empty, @"\b20\d{2}\b");
        return match.Success ? int.Parse(match.Value, CultureInfo.InvariantCulture) : null;
    }
}
