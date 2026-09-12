using System.Diagnostics;
using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Output;

namespace RevitModelMcp.Control;

internal static class ActionCommandExecutor
{
    internal static bool ActionsEnabled => File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelMcp", "allow-write"));

    public static void Execute(UIApplication application, ControlJobParseResult job, DateTimeOffset startedAt)
    {
        var stopwatch = Stopwatch.StartNew();
        CommandResponse<ActionResultData> response;
        var dialogsSuppressed = new List<string>();
        var viewOpened = false;
        var failures = new ActionFailures();
        void SuppressDialog(object? sender, DialogBoxShowingEventArgs arguments)
        {
            if (arguments is not TaskDialogShowingEventArgs dialog) return;
            if (dialog.OverrideResult((int)TaskDialogResult.Ok) || dialog.OverrideResult((int)TaskDialogResult.Yes))
                dialogsSuppressed.Add(dialog.Message);
        }
        application.DialogBoxShowing += SuppressDialog;
        try
        {
            if (!ActionsEnabled) throw new InvalidOperationException("actions disabled on the workstation");
            if (job.Error is not null) throw new ArgumentException(job.Error);
            var uiDocument = application.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var action = job.Action ?? throw new ArgumentException("Missing action arguments.");
            var document = uiDocument.Document;
            var ids = job.Command == "isolate" && action.Reset
                ? new List<ElementId>() : ResolveIds(document, action.ElementIds);
            ActionResultData data;
            if (job.Command is "select" or "show")
            {
                if (job.Command == "show")
                {
                    viewOpened = OpenViewForElements(uiDocument, ids);
                    uiDocument.ShowElements(ids);
                }
                if (job.Command == "select" || action.Select) uiDocument.Selection.SetElementIds(ids);
                data = new ActionResultData { Count = uiDocument.Selection.GetElementIds().Count };
            }
            else
            {
                using var transaction = new Transaction(document, "revit_" + job.Command.Replace('-', '_'));
                if (transaction.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Could not start the action transaction.");
                transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
                    .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                try
                {
                    data = Mutate(document, job.Command, action, ids);
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException(failures.Message ?? "The action transaction was rolled back.");
                }
                catch
                {
                    if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                    throw;
                }
            }
            response = CommandResponse<ActionResultData>.Ok(job.Command, data, stopwatch.ElapsedMilliseconds);
            response.WarningsDismissed = failures.WarningsDismissed;
        }
        catch (Exception exception)
        {
            response = CommandResponse<ActionResultData>.Fail(job.Command, exception.Message, stopwatch.ElapsedMilliseconds);
            response.Error = exception.Message;
            if (exception is FamilyNotLoadedException missing)
                response.Data = new ActionResultData { ClosestFamilies = missing.ClosestFamilies };
            PluginLog.Error($"Action failed. Command='{job.Command}'.", exception);
        }
        finally
        {
            application.DialogBoxShowing -= SuppressDialog;
        }
        response.DialogsSuppressed = dialogsSuppressed;
        if (job.Command == "show") response.ViewOpened = viewOpened;
        response.ActiveView = application.ActiveUIDocument?.ActiveView?.Name ?? string.Empty;
        CommandResponseFileWriter.Create(startedAt.LocalDateTime, job.Command,
            ReadCommandReader.ReadResponder(application)).Write(response);
    }

    public static void WriteError(UIApplication application, string command, string message, DateTimeOffset startedAt)
    {
        var error = ActionsEnabled ? message : "actions disabled on the workstation";
        var response = CommandResponse<ActionResultData>.Fail(command, error, 0);
        response.Error = error;
        response.DialogsSuppressed = [];
        if (command == "show") response.ViewOpened = false;
        response.ActiveView = application.ActiveUIDocument?.ActiveView?.Name ?? string.Empty;
        CommandResponseFileWriter.Create(startedAt.LocalDateTime, command,
            ReadCommandReader.ReadResponder(application)).Write(response);
    }

    private static bool OpenViewForElements(UIDocument uiDocument, List<ElementId> ids)
    {
        var document = uiDocument.Document;
        using var idFilter = new ElementIdSetFilter(ids);
        foreach (var uiView in uiDocument.GetOpenUIViews())
        {
            if (!FilteredElementCollector.IsViewValidForElementIteration(document, uiView.ViewId)) continue;
            using var visible = document.CollectElements(uiView.ViewId).WherePasses(idFilter);
            if (visible.Any()) return false;
        }

        View? target = null;
        var levels = ids.Select(id => id.ToElement(document)?.LevelId.ToElement<Level>(document))
            .Where(level => level is not null).Distinct().ToList();
        if (levels.Count > 0)
        {
            using var planCollector = document.CollectElements().OfClass<ViewPlan>();
            var plans = planCollector.Cast<ViewPlan>()
                .Where(view => !view.IsTemplate).ToList();
            foreach (var level in levels)
            {
                target = plans.Where(view => view.GenLevel?.Id == level!.Id)
                    .OrderByDescending(view => view.ViewType == ViewType.FloorPlan)
                    .ThenByDescending(view => view.Name.StartsWith(level!.Name, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (target is not null) break;
            }
        }
        if (target is null)
        {
            using var viewCollector = document.CollectElements().OfClass<View3D>();
            target = viewCollector.Cast<View3D>().FirstOrDefault(view => !view.IsTemplate);
        }
        if (target is null) throw new InvalidOperationException("No non-template level plan or 3D view is available to show these elements.");

        var wasOpen = uiDocument.GetOpenUIViews().Any(view => view.ViewId == target.Id);
        // The ExternalEvent runs without a transaction; ShowElements requires the view change immediately.
        uiDocument.ActiveView = target;
        return !wasOpen;
    }

    private static ActionResultData Mutate(Document document, string command, ActionJobContract action, List<ElementId> ids)
    {
        switch (command)
        {
            case "isolate":
                if (action.Reset) document.ActiveView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                else document.ActiveView.IsolateElementsTemporary(ids);
                return new ActionResultData { Count = ids.Count };
            case "move":
                ElementTransformUtils.MoveElements(document, ids, new XYZ(Millimeters(action.DxMm), Millimeters(action.DyMm), Millimeters(action.DzMm)));
                return new ActionResultData { Count = ids.Count };
            case "delete":
                return new ActionResultData { Count = document.Delete(ids).Count };
            case "place-family":
                return PlaceFamily(document, action);
            case "create-wall":
                return CreateWall(document, action);
            case "set-parameter":
                return SetParameter(document, action);
            default:
                throw new ArgumentException($"Unknown action: {command}.");
        }
    }

    private static ActionResultData PlaceFamily(Document document, ActionJobContract action)
    {
        using var symbols = document.CollectElements().OfClass<FamilySymbol>()
            .WhereParameter(BuiltInParameter.ALL_MODEL_FAMILY_NAME).Equals(action.Family!);
        if (!symbols.Any())
        {
            using var familyCollector = document.CollectElements().OfClass<Family>();
            var families = familyCollector.Cast<Family>().ToList();
            var categories = families.GroupBy(loaded => loaded.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,
                    group => string.Join(", ", group.Select(loaded => loaded.FamilyCategory?.Name ?? "Uncategorized").Distinct()),
                    StringComparer.OrdinalIgnoreCase);
            var suggestions = ActionJobParser.ClosestFamilyNames(action.Family!, categories.Keys)
                .Select(name => $"{name} ({categories[name]})").ToList();
            throw new FamilyNotLoadedException(action.Family!, suggestions);
        }
        if (action.TypeName is not null)
            symbols.WhereParameter(BuiltInParameter.SYMBOL_NAME_PARAM).Equals(action.TypeName);
        var symbol = symbols.FirstOrDefault() as FamilySymbol
                     ?? throw new ArgumentException($"Type '{action.TypeName}' was not found in family '{action.Family}'.");
        var level = FindLevel(document, action.Level!);
        if (!symbol.IsActive)
        {
            symbol.Activate();
            document.Regenerate();
        }
        var point = new XYZ(Millimeters(action.XMm), Millimeters(action.YMm), level.ProjectElevation);
        var instance = document.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);
        if (action.RotationDeg != 0)
            instance.Rotate(Line.CreateBound(point, point + XYZ.BasisZ), action.RotationDeg * Math.PI / 180);
        return new ActionResultData { Id = RevitValueReader.GetId(instance.Id), Category = instance.Category?.Name, Level = level.Name };
    }

    private static ActionResultData CreateWall(Document document, ActionJobContract action)
    {
        var level = FindLevel(document, action.Level!);
        using var types = document.CollectElements().OfClass<WallType>();
        if (action.WallType is not null)
            types.WhereParameter(BuiltInParameter.SYMBOL_NAME_PARAM).Equals(action.WallType);
        var wallType = types.Cast<WallType>().FirstOrDefault(candidate => action.WallType is not null || candidate.Kind == WallKind.Basic)
                       ?? throw new ArgumentException($"Wall type '{action.WallType ?? "basic wall"}' was not found.");
        var start = new XYZ(Millimeters(action.StartMm[0]), Millimeters(action.StartMm[1]), level.ProjectElevation);
        var end = new XYZ(Millimeters(action.EndMm[0]), Millimeters(action.EndMm[1]), level.ProjectElevation);
        var line = Line.CreateBound(start, end);
        var wall = Wall.Create(document, line, wallType.Id, level.Id, Millimeters(action.HeightMm), 0, false, false);
        return new ActionResultData { Id = RevitValueReader.GetId(wall.Id), LengthMm = line.Length.ToMillimeters() };
    }

    private static ActionResultData SetParameter(Document document, ActionJobContract action)
    {
        var element = CreateId(action.ElementId).ToElement(document)
                      ?? throw new ArgumentException($"Element {action.ElementId} was not found.");
        var parameter = element.FindParameter(action.Parameter!)
                        ?? throw new ArgumentException($"Parameter '{action.Parameter}' was not found on the instance or type.");
        if (parameter.IsReadOnly) throw new InvalidOperationException($"Parameter '{action.Parameter}' is read-only.");
        var oldValue = ParameterValue(parameter);
        var value = action.Value!;
        var changed = parameter.StorageType switch
        {
            StorageType.String => parameter.Set(value),
            StorageType.Integer => parameter.Set(int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            StorageType.Double => parameter.Set(ParameterDouble(parameter, value)),
            _ => throw new ArgumentException("Only String, Integer and Double parameters are supported; ElementId parameters cannot be set.")
        };
        if (!changed)
            throw new InvalidOperationException($"Revit could not set parameter '{action.Parameter}'.");
        return new ActionResultData
        {
            OldValue = oldValue,
            NewValue = ParameterValue(parameter),
            ParameterScope = parameter.Element.Id == element.Id ? "instance" : "type"
        };
    }

    private static double ParameterDouble(Parameter parameter, string text)
    {
        var value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("Parameter value must be finite.");
        var spec = parameter.Definition.GetDataType();
        if (spec == SpecTypeId.Length) return Millimeters(value);
        if (spec == SpecTypeId.Area) return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.SquareMeters);
        return value;
    }

    private static string ParameterValue(Parameter parameter)
    {
        if (!parameter.HasValue) return string.Empty;
        if (parameter.StorageType == StorageType.String) return parameter.AsString() ?? string.Empty;
        if (parameter.StorageType == StorageType.Integer) return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
        if (parameter.StorageType != StorageType.Double) throw new ArgumentException("Unsupported parameter storage type.");
        var spec = parameter.Definition.GetDataType();
        var value = parameter.AsDouble();
        if (spec == SpecTypeId.Length) value = value.ToMillimeters();
        else if (spec == SpecTypeId.Area) value = UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.SquareMeters);
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static Level FindLevel(Document document, string name)
    {
        using var levels = document.CollectElements().OfClass<Level>()
            .WhereParameter(BuiltInParameter.DATUM_TEXT).Equals(name);
        return levels.FirstOrDefault() as Level
               ?? throw new ArgumentException($"Level '{name}' was not found.");
    }

    private static List<ElementId> ResolveIds(Document document, IEnumerable<long> values)
    {
        var ids = values.Select(CreateId).ToList();
        foreach (var id in ids)
            if (id.ToElement(document) is null) throw new ArgumentException($"Element {RevitValueReader.GetId(id)} was not found.");
        return ids;
    }

    private static ElementId CreateId(long value)
    {
#if REVIT2024_OR_GREATER
        return new ElementId(value);
#else
        return new ElementId(checked((int)value));
#endif
    }

    private static double Millimeters(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);

    private sealed class FamilyNotLoadedException(string name, List<string> closestFamilies)
        : ArgumentException($"Family '{name}' is not loaded. Closest loaded families: {string.Join(", ", closestFamilies)}")
    {
        public List<string> ClosestFamilies { get; } = closestFamilies;
    }

    private sealed class ActionFailures : IFailuresPreprocessor
    {
        public List<string> WarningsDismissed { get; } = [];
        public string? Message { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var errors = new List<string>();
            var resolved = false;
            var rollBack = false;
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                var severity = failure.GetSeverity();
                if (severity == FailureSeverity.None) continue;
                var description = failure.GetDescriptionText();
                var resolution = FailureResolutionType.Invalid;
                // Other resolutions can delete, detach, skip or move elements outside the requested action.
                if (severity == FailureSeverity.Error && failure.HasResolutions())
                {
                    foreach (var candidate in new[] { FailureResolutionType.FixElements, FailureResolutionType.SetValue })
                    {
                        if (!failure.HasResolutionOfType(candidate)
                            || !failuresAccessor.IsFailureResolutionPermitted(failure, candidate)) continue;
                        resolution = candidate;
                        break;
                    }
                }
                var disposition = ActionFailurePolicy.Classify(
                    severity == FailureSeverity.Warning, severity == FailureSeverity.Error,
                    resolution != FailureResolutionType.Invalid,
                    severity == FailureSeverity.Error && failuresAccessor.GetAttemptedResolutionTypes(failure).Count > 0);
                if (disposition == ActionFailureDisposition.DismissWarning)
                {
                    failuresAccessor.DeleteWarning(failure);
                    WarningsDismissed.Add(description);
                    continue;
                }

                errors.Add(description);
                if (disposition == ActionFailureDisposition.ResolveError)
                {
                    try
                    {
                        failure.SetCurrentResolutionType(resolution);
                        failuresAccessor.ResolveFailure(failure);
                        resolved = true;
                        continue;
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException exception)
                    {
                        PluginLog.Error("Action failure resolution was rejected; rolling back.", exception);
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException exception)
                    {
                        PluginLog.Error("Action failure resolution was unavailable; rolling back.", exception);
                    }
                }
                rollBack = true;
            }
            Message = errors.Count > 0 ? string.Join("; ", errors) : null;
            if (rollBack) return FailureProcessingResult.ProceedWithRollBack;
            return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
        }
    }
}
