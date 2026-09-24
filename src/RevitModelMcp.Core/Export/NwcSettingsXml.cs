using System.Globalization;
using System.Runtime.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace RevitModelMcp.Core.Export;

[DataContract]
public sealed record NwcUnappliedOption(
    [property: DataMember(Name = "id")] string Id,
    [property: DataMember(Name = "value")] object Value,
    [property: DataMember(Name = "reason")] string Reason);

[DataContract]
public sealed record NwcIgnoredOption(
    [property: DataMember(Name = "id")] string Id,
    [property: DataMember(Name = "value")] object Value);

[DataContract]
public sealed record NwcInvalidOption(
    [property: DataMember(Name = "id")] string Id,
    [property: DataMember(Name = "value")] object? Value,
    [property: DataMember(Name = "reason")] string Reason);

[DataContract]
public sealed record NwcSettingsResult(
    [property: DataMember(Name = "values")] Dictionary<string, object> Values,
    [property: DataMember(Name = "mapping")] Dictionary<string, string> Mapping,
    [property: DataMember(Name = "notApplied")] List<NwcUnappliedOption> NotApplied,
    [property: DataMember(Name = "ignored")] List<NwcIgnoredOption> Ignored,
    [property: DataMember(Name = "invalid")] List<NwcInvalidOption> Invalid);

/// <summary>
/// Parses a Navisworks exporter settings XML file. The exact encoding of some options was unconfirmed
/// until live testing against a real export: enum options (parameters/scope/coordinates) may carry
/// their ordinal either as a <c>:&lt;n&gt;</c> suffix on the option's <c>name</c> attribute, or as a
/// <c>"&lt;id&gt;:&lt;n&gt;"</c> string in the &lt;data&gt; value itself. A value that cannot be parsed
/// under any known encoding is reported in <see cref="NwcSettingsResult.Invalid"/> instead of aborting
/// the whole file.
/// </summary>
public static class NwcSettingsXml
{
    private const string Prefix = "nwexportrevit_";

    private static readonly string[] Parameters = ["none", "elements", "all"];
    private static readonly string[] Scopes = ["model", "view", "selection"];
    private static readonly string[] Coordinates = ["shared", "internal"];

    public static NwcSettingsResult ReadFile(string path)
    {
        var normalized = NwcPathValidator.EnsureAbsoluteNoTraversal(path, "settingsXml");
        if (!normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("settingsXml must have the .xml extension.", nameof(path));
        try
        {
            return Parse(File.ReadAllText(normalized));
        }
        catch (IOException)
        {
            throw new IOException("Could not read the NWC settings XML file on the Revit workstation.");
        }
    }

    public static NwcSettingsResult Parse(string xml)
    {
        if (xml is null) throw new ArgumentNullException(nameof(xml));
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        var document = XDocument.Load(reader);
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var notApplied = new List<NwcUnappliedOption>();
        var ignored = new List<NwcIgnoredOption>();
        var invalid = new List<NwcInvalidOption>();

        foreach (var option in document.Descendants().Where(element => element.Name.LocalName == "option"))
        {
            var id = (string?)option.Attribute("name");
            var data = option.Elements().FirstOrDefault(element => element.Name.LocalName == "data");
            if (string.IsNullOrWhiteSpace(id) || data is null) continue;
            if (!TryReadValue(data, out var value, out var readReason))
            {
                invalid.Add(new NwcInvalidOption(id!, data.Value.Trim(), readReason!));
                continue;
            }
            var key = id!.Split(':')[0];
            if (value is false && key is ("nwexportrevit_element_params" or "nwexportrevit_section_extract" or "nwexportrevit_coordinates")) continue;
            if (!key.StartsWith(Prefix, StringComparison.Ordinal))
            {
                ignored.Add(new NwcIgnoredOption(id!, value));
                continue;
            }

            var target = key switch
            {
                "nwexportrevit_element_params" => ("parameters", (IReadOnlyList<string>?)Parameters),
                "nwexportrevit_section_extract" => ("scope", Scopes),
                "nwexportrevit_coordinates" => ("coordinates", Coordinates),
                "nwexportrevit_element_ids" => ("export_element_ids", null),
                "nwexportrevit_element_find_missing_materials" => ("find_missing_materials", null),
                "nwexportrevit_element_generic_properties" => ("convert_element_properties", null),
                "nwexportrevit_urls" => ("export_urls", null),
                "nwexportrevit_room" => ("export_room_as_attribute", null),
                "nwexportrevit_room_geometry" => ("export_room_geometry", null),
                "nwexportrevit_linked_files" => ("export_links", null),
                "nwexportrevit_construction_parts" => ("export_parts", null),
                "nwexportrevit_divide_file_into_levels" => ("divide_file_into_levels", null),
                "nwexportrevit_param_faceting_factor" => ("faceting_factor", null),
                "nwexportrevit_linked_CAD_formats" => ("convert_linked_cad_formats", null),
                "nwexportrevit_lights" => ("convert_lights", null),
                _ => ((string?)null, null)
            };
            if (target.Item1 is not null)
            {
                if (target.Item2 is not null)
                {
                    if (!TryResolveEnum(target.Item2, id!, value, out var enumValue))
                    {
                        invalid.Add(new NwcInvalidOption(id!, value, $"unrecognized enum value for {target.Item1}"));
                        continue;
                    }
                    values[target.Item1] = enumValue;
                }
                else values[target.Item1] = value;
                mapping[id!] = target.Item1;
            }
            else if (key is "nwexportrevit_embed_textures" or "nwexportrevit_with_type_props"
                     or "nwexportrevit_separate_custom_props" or "nwexportrevit_strict_sectioning")
                notApplied.Add(new NwcUnappliedOption(id!, value, "no Revit API property"));
            else
                ignored.Add(new NwcIgnoredOption(id!, value));
        }

        return new NwcSettingsResult(values, mapping, notApplied, ignored, invalid);
    }

    private static bool TryReadValue(XElement data, out object value, out string? reason)
    {
        var text = data.Value.Trim();
        switch ((string?)data.Attribute("type"))
        {
            case "bool" when text is "true" or "1":
                value = true; reason = null; return true;
            case "bool" when text is "false" or "0":
                value = false; reason = null; return true;
            case "int32" when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i):
                value = i; reason = null; return true;
            case "float" when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d):
                value = d; reason = null; return true;
            case "wstring":
                value = text; reason = null; return true;
            case null:
                // Some exporters omit the type attribute; sniff the text for a plausible encoding.
                if (text is "true" or "false") { value = text == "true"; reason = null; return true; }
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fallbackInt))
                { value = fallbackInt; reason = null; return true; }
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fallbackFloat))
                { value = fallbackFloat; reason = null; return true; }
                if (text.Length > 0) { value = text; reason = null; return true; }
                break;
        }
        value = null!;
        reason = "unsupported or malformed NWC settings XML data value";
        return false;
    }

    /// <summary>
    /// Resolves an enum ordinal from, in order: a <c>:&lt;n&gt;</c> suffix on the option id (the
    /// originally assumed encoding), then the value itself when numeric or boolean, then a
    /// <c>"&lt;id&gt;:&lt;n&gt;"</c> string embedded in the value (the encoding a real Navisworks
    /// export was found to use).
    /// </summary>
    private static bool TryResolveEnum(IReadOnlyList<string> choices, string id, object value, out string enumValue)
    {
        var idColon = id.LastIndexOf(':');
        if (idColon >= 0 && TryOrdinal(id.Substring(idColon + 1), choices.Count, out var fromId))
        {
            enumValue = choices[fromId];
            return true;
        }
        var candidate = value switch
        {
            int i => i.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            bool b => (b ? 1 : 0).ToString(CultureInfo.InvariantCulture),
            string s => s,
            _ => null
        };
        if (candidate is not null)
        {
            var valueColon = candidate.LastIndexOf(':');
            var suffix = valueColon >= 0 ? candidate.Substring(valueColon + 1) : candidate;
            if (TryOrdinal(suffix, choices.Count, out var fromValue))
            {
                enumValue = choices[fromValue];
                return true;
            }
        }
        enumValue = string.Empty;
        return false;
    }

    private static bool TryOrdinal(string text, int count, out int ordinal) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal) && ordinal >= 0 && ordinal < count;
}
