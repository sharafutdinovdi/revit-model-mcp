using System.Diagnostics;
using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Output;

namespace RevitModelMcp.Control;

internal static class ActionCommandExecutor
{
    private static bool ActionsEnabled => File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelMcp", "allow-write"));

    public static void Execute(UIApplication application, ControlJobParseResult job, DateTimeOffset startedAt)
    {
        var stopwatch = Stopwatch.StartNew();
        CommandResponse<ActionResultData> response;
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
                if (job.Command == "show") uiDocument.ShowElements(ids);
                if (job.Command == "select" || action.Select) uiDocument.Selection.SetElementIds(ids);
                data = new ActionResultData { Count = uiDocument.Selection.GetElementIds().Count };
            }
            else
            {
                using var transaction = new Transaction(document, "revit_" + job.Command.Replace('-', '_'));
                if (transaction.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Could not start the action transaction.");
                var failures = new RollbackFailures();
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
        }
        catch (Exception exception)
        {
            response = CommandResponse<ActionResultData>.Fail(job.Command, exception.Message, stopwatch.ElapsedMilliseconds);
            response.Error = exception.Message;
            if (exception is FamilyNotLoadedException missing)
                response.Data = new ActionResultData { ClosestFamilies = missing.ClosestFamilies };
            PluginLog.Error($"Action failed. Command='{job.Command}'.", exception);
        }
        response.ActiveView = application.ActiveUIDocument?.ActiveView?.Name ?? string.Empty;
        CommandResponseFileWriter.Create(startedAt.LocalDateTime, job.Command,
            ReadCommandReader.ReadResponder(application)).Write(response);
    }

    public static void WriteError(UIApplication application, string command, string message, DateTimeOffset startedAt)
    {
        var error = ActionsEnabled ? message : "actions disabled on the workstation";
        var response = CommandResponse<ActionResultData>.Fail(command, error, 0);
        response.Error = error;
        response.ActiveView = application.ActiveUIDocument?.ActiveView?.Name ?? string.Empty;
        CommandResponseFileWriter.Create(startedAt.LocalDateTime, command,
            ReadCommandReader.ReadResponder(application)).Write(response);
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
        var symbols = document.CollectElements().OfClass<FamilySymbol>()
            .WhereParameter(BuiltInParameter.ALL_MODEL_FAMILY_NAME).Equals(action.Family!);
        if (!symbols.Any())
        {
            var names = document.CollectElements().OfClass<Family>().Cast<Family>()
                .Select(loaded => loaded.Name);
            throw new FamilyNotLoadedException(action.Family!, ActionJobParser.ClosestFamilyNames(action.Family!, names));
        }
        symbols = document.CollectElements().OfClass<FamilySymbol>()
            .WhereParameter(BuiltInParameter.ALL_MODEL_FAMILY_NAME).Equals(action.Family!);
        if (action.TypeName is not null)
            symbols = symbols.WhereParameter(BuiltInParameter.SYMBOL_NAME_PARAM).Equals(action.TypeName);
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
        var types = document.CollectElements().OfClass<WallType>();
        if (action.WallType is not null)
            types = types.WhereParameter(BuiltInParameter.SYMBOL_NAME_PARAM).Equals(action.WallType);
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
            OldValue = oldValue, NewValue = ParameterValue(parameter),
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

    private static Level FindLevel(Document document, string name) =>
        document.CollectElements().OfClass<Level>()
            .WhereParameter(BuiltInParameter.DATUM_TEXT).Equals(name).FirstOrDefault() as Level
        ?? throw new ArgumentException($"Level '{name}' was not found.");

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

    private sealed class RollbackFailures : IFailuresPreprocessor
    {
        public string? Message { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var messages = failuresAccessor.GetFailureMessages();
            if (messages.Count == 0) return FailureProcessingResult.Continue;
            Message = string.Join("; ", messages.Select(message => message.GetDescriptionText()));
            return FailureProcessingResult.ProceedWithRollBack;
        }
    }
}
