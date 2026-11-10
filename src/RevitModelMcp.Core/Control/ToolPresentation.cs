using System.Globalization;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RevitModelMcp.Core.Control;

[DataContract]
public sealed record ToolPresentation
{
    [DataMember] public string Label { get; init; } = "";
    [DataMember] public string Icon { get; init; } = "Mytools";
    [DataMember] public bool Hidden { get; init; }
    [DataMember] public int Order { get; init; }
    [DataMember] public string LastArguments { get; init; } = "{}";
    [DataMember] public string SmallPng { get; init; } = "";
    [DataMember] public string LargePng { get; init; } = "";
}

public sealed class ToolPresentationStore(string directory)
{
    public static IReadOnlyList<string> BuiltInIcons { get; } = Array.AsReadOnly(new[] { "Mytools", "Activity", "Settings", "Help", "Connect", "About" });

    public static ToolPresentation Defaults(string name)
    {
        ToolStore.ValidateName(name);
        var label = name.Replace('_', ' ');
        var hash = ToolDefinition.Hash(name);
        return new ToolPresentation
        {
            Label = char.ToUpperInvariant(label[0]) + label[1..],
            Icon = BuiltInIcons[Convert.ToInt32(hash[..4], 16) % BuiltInIcons.Count]
        };
    }

    public ToolPresentation Get(string name)
    {
        var fallback = Defaults(name);
        try
        {
            var path = Path.Combine(directory, name + ".json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return fallback;
            var presentation = ToolJson.Deserialize<ToolPresentation>(File.ReadAllText(path));
            Validate(presentation);
            return presentation;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SerializationException or XmlException or ArgumentException or FormatException)
        {
            return fallback;
        }
    }

    public void Save(string name, ToolPresentation presentation)
    {
        ToolStore.ValidateName(name);
        Validate(presentation);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(ToolJson.Serialize(presentation));
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Delete(string name)
    {
        ToolStore.ValidateName(name);
        File.Delete(Path.Combine(directory, name + ".json"));
    }

    public static void Validate(ToolPresentation presentation)
    {
        if (presentation is null) throw new ArgumentNullException(nameof(presentation));
        ToolDefinition.RejectFormat(presentation.Label);
        if (string.IsNullOrWhiteSpace(presentation.Label) || presentation.Label.Length > 80)
            throw new ArgumentException("Display label must contain 1 to 80 characters.");
        if (presentation.Label.Any(char.IsControl)) throw new ArgumentException("Display label cannot contain control characters.");
        if (!BuiltInIcons.Contains(presentation.Icon)) throw new ArgumentException("Choose a built-in icon.");
        if (presentation.LastArguments is null || Encoding.UTF8.GetByteCount(presentation.LastArguments) > 65536)
            throw new ArgumentException("Last arguments exceed 64 KB.");
        using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(presentation.LastArguments), XmlDictionaryReaderQuotas.Max);
        if ((string?)XElement.Load(reader).Attribute("type") != "object") throw new ArgumentException("Last arguments must be an object.");
        foreach (var png in new[] { presentation.SmallPng, presentation.LargePng })
        {
            if (png is null || png.Length > 400000) throw new ArgumentException("Stored icon is too large.");
            if (png.Length != 0) Convert.FromBase64String(png);
        }
    }
}

public static class ToolRibbonSlots
{
    public const int Capacity = 24;

    public static IReadOnlyList<string> Assign(IEnumerable<string> names, Func<string, ToolPresentation> presentation)
    {
        if (names is null) throw new ArgumentNullException(nameof(names));
        if (presentation is null) throw new ArgumentNullException(nameof(presentation));
        return names.Distinct(StringComparer.Ordinal).Select(name => (Name: name, Presentation: presentation(name)))
            .Where(tool => !tool.Presentation.Hidden)
            .OrderBy(tool => tool.Presentation.Order).ThenBy(tool => tool.Name, StringComparer.Ordinal)
            .Take(Capacity).Select(tool => tool.Name).ToArray();
    }
}

public sealed record ToolParameterField(string Name, string Type, string Description, bool Required,
    IReadOnlyList<object> Choices, string? Minimum, string? Maximum, int? MaxLength);

public sealed class ToolParameterForm
{
    private readonly string _schema;
    public IReadOnlyList<ToolParameterField> Fields { get; }

    public ToolParameterForm(string schema)
    {
        ToolArguments.ValidateSchema(schema);
        _schema = schema;
        using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(schema), XmlDictionaryReaderQuotas.Max);
        var root = XElement.Load(reader);
        var required = root.Element("required")?.Elements().Select(item => item.Value).ToArray() ?? [];
        Fields = root.Element("properties")?.Elements().Select(property => new ToolParameterField(
            (string?)property.Attribute("item") ?? property.Name.LocalName,
            property.Element("type")!.Value,
            property.Element("description")?.Value ?? "",
            required.Contains((string?)property.Attribute("item") ?? property.Name.LocalName),
            property.Element("enum")?.Elements().Select(Value).ToArray() ?? [],
            property.Element("minimum")?.Value,
            property.Element("maximum")?.Value,
            property.Element("maxLength") is XElement maximumLength ? (int?)Math.Min(int.MaxValue, double.Parse(maximumLength.Value, CultureInfo.InvariantCulture)) : null)).ToArray() ?? [];
    }

    private static object Value(XElement element) => (string?)element.Attribute("type") switch
    {
        "boolean" => element.Value == "true",
        "number" => decimal.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("G29", CultureInfo.InvariantCulture)
            : double.Parse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
        _ => element.Value
    };

    public string Validate(IReadOnlyDictionary<string, string?> values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        var arguments = new Dictionary<string, object>();
        foreach (var pair in values)
        {
            var field = Fields.FirstOrDefault(candidate => candidate.Name == pair.Key)
                ?? throw new ArgumentException($"args.{pair.Key}: unknown parameter.");
            if (pair.Value is null) continue;
            object value;
            if (field.Type == "string") value = pair.Value;
            else if (field.Type == "boolean" && bool.TryParse(pair.Value, out var boolean)) value = boolean;
            else if (field.Type == "integer" && long.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) value = integer;
            else if (field.Type == "number" && double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && !double.IsNaN(number) && !double.IsInfinity(number)) value = number;
            else throw new ArgumentException($"args.{pair.Key}: expected {field.Type}.");
            arguments.Add(pair.Key, value);
        }
        var json = CodeResultLimiter.ToJson(arguments);
        ToolArguments.Validate(_schema, json);
        return json;
    }
}

public sealed class ToolRibbonSession
{
    private readonly HashSet<string> _confirmed = new(StringComparer.Ordinal);
    public bool RequiresReview(string name, string revision) => !_confirmed.Contains(name + ":" + revision);
    public void Confirm(string name, string revision) => _confirmed.Add(name + ":" + revision);
    public static string? Refusal(bool locked, string lockMessage, string? policyRefusal) => locked ? lockMessage : policyRefusal;
}
