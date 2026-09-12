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
    private readonly HashSet<long> _warningIds;

    public ElementFieldReader(Document document)
    {
        _document = document;
        _warningIds = ReadCommandReader.ReadWarningElementIds(document);
    }

    public static bool IsBuiltInField(string name) => BuiltInFields.Contains(name);

    public PreparedQueryRecord Prepare(Element element, IEnumerable<string> fields)
    {
        var record = new PreparedQueryRecord { Id = RevitValueReader.GetId(element.Id) };
        foreach (var field in fields.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(field, "id", StringComparison.OrdinalIgnoreCase))
            {
                record.Values[field] = new PreparedQueryValue
                {
                    Text = record.Id.ToString(CultureInfo.InvariantCulture),
                    Number = record.Id
                };
                continue;
            }

            record.Values[field] = ReadValue(element, field);
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

    private PreparedQueryValue ReadValue(Element element, string field)
    {
        if (string.Equals(field, "category", StringComparison.OrdinalIgnoreCase))
        {
            return Text(element.Category?.Name);
        }

        if (string.Equals(field, "family", StringComparison.OrdinalIgnoreCase))
        {
            return Text(RevitValueReader.GetFamilyName(element));
        }

        if (string.Equals(field, "type", StringComparison.OrdinalIgnoreCase))
        {
            return Text(RevitValueReader.GetTypeName(_document, element));
        }

        if (string.Equals(field, "name", StringComparison.OrdinalIgnoreCase))
        {
            return Text(element.Name);
        }

        if (string.Equals(field, "level", StringComparison.OrdinalIgnoreCase))
        {
            return Text(ReadLevel(element));
        }

        if (string.Equals(field, "workset", StringComparison.OrdinalIgnoreCase))
        {
            return Text(ReadWorkset(element));
        }

        if (string.Equals(field, "phase", StringComparison.OrdinalIgnoreCase))
        {
            return Text(ReadPhase(element));
        }

        if (string.Equals(field, "areaScheme", StringComparison.OrdinalIgnoreCase))
        {
            return Text((element as Area)?.AreaScheme?.Name);
        }

        if (string.Equals(field, "hasWarnings", StringComparison.OrdinalIgnoreCase))
        {
            var hasWarnings = _warningIds.Contains(RevitValueReader.GetId(element.Id));
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
            var type = _document.GetElement(element.GetTypeId());
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
