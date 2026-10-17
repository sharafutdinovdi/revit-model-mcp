using System.Globalization;
using Autodesk.Revit.DB;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal sealed class ElementFieldReader
{
    private static readonly HashSet<string> BuiltInFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "category", "family", "type", "name", "level", "workset", "phase", "areaScheme", "hasWarnings"
    };

    private readonly Document _document;
    private readonly Lazy<HashSet<long>> _warningIds;
    private readonly Dictionary<ElementId, Element?> _typeElements = new();
    private readonly List<FieldSelection> _fieldSelections = new();

    private enum FieldKind
    {
        Parameter, Id, Category, Family, Type, Name, Level, Workset, Phase, AreaScheme, HasWarnings
    }

    private sealed class FieldSelection
    {
        public readonly IEnumerable<string> Source;
        public readonly string[] Requested;
        public readonly KeyValuePair<string, FieldKind>[] Fields;

        public FieldSelection(IEnumerable<string> fields)
        {
            Source = fields;
            Requested = fields.ToArray();
            Fields = Requested.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(field => new KeyValuePair<string, FieldKind>(field,
                    BuiltInFields.Contains(field) && Enum.TryParse<FieldKind>(field, true, out var kind)
                        ? kind : FieldKind.Parameter))
                .ToArray();
        }
    }

    public ElementFieldReader(Document document)
    {
        _document = document;
        _warningIds = new Lazy<HashSet<long>>(() => ReadCommandReader.ReadWarningElementIds(document));
    }

    public static bool IsBuiltInField(string name) => BuiltInFields.Contains(name);

    public PreparedQueryRecord Prepare(Element element, IEnumerable<string> fields)
    {
        var record = new PreparedQueryRecord { Id = RevitValueReader.GetId(element.Id) };
        FieldSelection? selection = null;
        foreach (var candidate in _fieldSelections)
        {
            if (ReferenceEquals(candidate.Source, fields) || candidate.Requested.SequenceEqual(fields))
            {
                selection = candidate;
                break;
            }
        }
        if (selection is null)
        {
            selection = new FieldSelection(fields);
            _fieldSelections.Add(selection);
        }
        foreach (var requestedField in selection.Fields)
        {
            var field = requestedField.Key;
            if (requestedField.Value == FieldKind.Id)
            {
                record.Values[field] = new PreparedQueryValue
                {
                    Text = record.Id.ToString(CultureInfo.InvariantCulture),
                    Number = record.Id
                };
                continue;
            }

            try
            {
                record.Values[field] = ReadValue(element, field, requestedField.Value);
            }
            catch (Exception exception)
            {
                PluginLog.Skipped($"element {record.Id} field {field}", exception);
                record.Values[field] = new PreparedQueryValue();
            }
        }

        return record;
    }

    public QueryElementItem ToOutput(PreparedQueryRecord record)
    {
        return new QueryElementItem
        {
            Id = record.Id,
            Values = record.Values.ToDictionary(
                pair => pair.Key,
                pair => new QueryFieldValue
                {
                    HasValue = pair.Value.Text is not null || pair.Value.Number.HasValue,
                    Value = pair.Value.Text,
                    NumericValue = pair.Value.Number,
                    Unit = pair.Value.Unit,
                    Source = pair.Value is ParameterPreparedValue parameter ? parameter.Source : "element"
                },
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private PreparedQueryValue ReadValue(Element element, string field, FieldKind kind)
    {
        if (kind == FieldKind.Category)
        {
            return Text(element.Category?.Name);
        }

        if (kind == FieldKind.Family)
        {
            return Text(RevitValueReader.GetFamilyName(element));
        }

        if (kind == FieldKind.Type)
        {
            return Text(RevitValueReader.GetTypeName(_document, element));
        }

        if (kind == FieldKind.Name)
        {
            return Text(element.Name);
        }

        if (kind == FieldKind.Level)
        {
            return Text(ReadLevel(element));
        }

        if (kind == FieldKind.Workset)
        {
            return Text(ReadWorkset(element));
        }

        if (kind == FieldKind.Phase)
        {
            return Text(ReadPhase(element));
        }

        if (kind == FieldKind.AreaScheme)
        {
            return Text((element as Area)?.AreaScheme?.Name);
        }

        if (kind == FieldKind.HasWarnings)
        {
            var hasWarnings = _warningIds.Value.Contains(RevitValueReader.GetId(element.Id));
            return new PreparedQueryValue { Text = hasWarnings ? "true" : "false", Number = hasWarnings ? 1 : 0 };
        }

        return ReadParameter(element, field);
    }

    private PreparedQueryValue ReadParameter(Element element, string name)
    {
        var parameter = FindParameter(element, name);
        var source = "instance";
        if (parameter is null)
        {
            var typeId = element.GetTypeId();
            if (!_typeElements.TryGetValue(typeId, out var type))
            {
                type = _document.GetElement(typeId);
                _typeElements.Add(typeId, type);
            }
            parameter = type is null ? null : FindParameter(type, name);
            source = "type";
        }

        if (parameter is null || !parameter.HasValue)
        {
            return new ParameterPreparedValue { Source = source };
        }

        var detail = ViewElementReader.ReadParameterDetail(parameter);
        var number = detail.MetricValue;
        var unit = detail.MetricUnit;
        if (!number.HasValue && parameter.StorageType == StorageType.Double)
        {
            number = parameter.AsDouble();
            unit = "internal";
        }
        else if (!number.HasValue && parameter.StorageType == StorageType.Integer)
        {
            number = parameter.AsInteger();
        }

        return new ParameterPreparedValue
        {
            Text = detail.Value ?? detail.InternalValue,
            Number = number,
            Unit = unit,
            Source = source
        };
    }

    private string? ReadLevel(Element element)
    {
        return RevitValueReader.IsValidId(element.LevelId)
            ? _document.GetElement(element.LevelId)?.Name
            : RevitValueReader.GetParameterText(element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)) ??
              RevitValueReader.GetParameterText(element.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM));
    }

    private string? ReadWorkset(Element element)
    {
        return _document.IsWorkshared ? _document.GetWorksetTable().GetWorkset(element.WorksetId)?.Name : null;
    }

    private string? ReadPhase(Element element)
    {
        var id = element.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId();
        return RevitValueReader.IsValidId(id) ? _document.GetElement(id)?.Name : null;
    }

    private static Parameter? FindParameter(Element element, string name)
    {
        return element.Parameters.Cast<Parameter>().FirstOrDefault(parameter =>
            string.Equals(parameter.Definition?.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static PreparedQueryValue Text(string? value) => new() { Text = value };

    private sealed class ParameterPreparedValue : PreparedQueryValue
    {
        public string Source { get; set; } = string.Empty;
    }
}
