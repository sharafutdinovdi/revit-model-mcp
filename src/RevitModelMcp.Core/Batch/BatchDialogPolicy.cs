using System.Runtime.Serialization.Json;
using System.Xml;
using System.Xml.Linq;

namespace RevitModelMcp.Core.Batch;

public sealed record BatchDialogDecision(bool Allowed, int? OverrideResult, bool Recycle);

public static class BatchDialogPolicy
{
    private static readonly IReadOnlyDictionary<(string Id, string Type), int> SafeChoices =
        new Dictionary<(string, string), int>();

    public const int MaximumRecords = 20;

    public static IReadOnlyDictionary<(string Id, string Type), int> Load(string path,
        IReadOnlyDictionary<(string Id, string Type), int>? builtInChoices = null)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        var choices = (builtInChoices ?? SafeChoices).ToDictionary(pair => pair.Key, pair => pair.Value);
        try
        {
            var bytes = File.ReadAllBytes(path);
            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            using var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, offset, bytes.Length - offset,
                XmlDictionaryReaderQuotas.Max);
            var root = XElement.Load(reader);
            if ((string?)root.Attribute("type") != "array") throw new FormatException("Root must be an array.");
            foreach (var entry in root.Elements())
            {
                if (entry.Name.LocalName != "item" || (string?)entry.Attribute("type") != "object")
                    throw new FormatException("Each entry must be an object.");
                var fields = entry.Elements().ToArray();
                if (fields.Length != 3 || fields.Select(field => field.Name.LocalName).Distinct(StringComparer.Ordinal).Count() != 3 ||
                    fields.Any(field => field.Name.LocalName is not ("dialogId" or "type" or "result")))
                    throw new FormatException("Each entry must contain exactly dialogId, type, and result.");
                var id = fields.Single(field => field.Name.LocalName == "dialogId");
                var type = fields.Single(field => field.Name.LocalName == "type");
                var result = fields.Single(field => field.Name.LocalName == "result");
                if ((string?)id.Attribute("type") != "string" || string.IsNullOrWhiteSpace(id.Value) ||
                    (string?)type.Attribute("type") != "string" || string.IsNullOrWhiteSpace(type.Value))
                    throw new FormatException("dialogId and type must be nonblank strings.");
                if ((string?)result.Attribute("type") != "number" || !int.TryParse(result.Value,
                        System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    throw new FormatException("result must be an integer.");
                if (!choices.TryAdd((id.Value, type.Value), value))
                    throw new FormatException("Duplicate dialogId and type pair.");
            }
            return choices;
        }
        catch (FileNotFoundException) { return choices; }
        catch (DirectoryNotFoundException) { return choices; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or FormatException)
        {
            throw new FormatException($"Invalid batch dialog allowlist '{path}': {exception.Message}", exception);
        }
    }

    public static void Append(BatchModel model, IEnumerable<BatchDialogRecord> records)
    {
        foreach (var record in records)
        {
            if (model.Dialogs.Count >= MaximumRecords) break;
            model.Dialogs.Add(record);
        }
    }

    public static BatchDialogDecision Decide(string? dialogId, string? runtimeType,
        IReadOnlyDictionary<(string Id, string Type), int>? allowlist = null) =>
        dialogId is not null && runtimeType is not null &&
        (allowlist ?? SafeChoices).TryGetValue((dialogId, runtimeType), out var choice)
            ? new(true, choice, false) : new(false, null, true);
}
