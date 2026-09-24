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
public sealed record NwcSettingsResult(
    [property: DataMember(Name = "values")] Dictionary<string, object> Values,
    [property: DataMember(Name = "mapping")] Dictionary<string, string> Mapping,
    [property: DataMember(Name = "notApplied")] List<NwcUnappliedOption> NotApplied,
    [property: DataMember(Name = "ignored")] List<NwcIgnoredOption> Ignored);

public static class NwcSettingsXml
{
    private const string Prefix = "nwexportrevit_";

    // Confirm these three enum orders against an XML exported by the installed Navisworks exporter.
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

        foreach (var option in document.Descendants().Where(element => element.Name.LocalName == "option"))
        {
            var id = (string?)option.Attribute("name");
            var data = option.Elements().FirstOrDefault(element => element.Name.LocalName == "data");
            if (string.IsNullOrWhiteSpace(id) || data is null) continue;
            var value = ReadValue(data);
            var key = id!.Split(':')[0];
            if (value is false && key is ("nwexportrevit_element_params" or "nwexportrevit_section_extract" or "nwexportrevit_coordinates")) continue;
            if (!key.StartsWith(Prefix, StringComparison.Ordinal))
            {
                ignored.Add(new NwcIgnoredOption(id, value));
                continue;
            }

            var mapped = key switch
            {
                "nwexportrevit_element_params" => ("parameters", EnumValue(Parameters, id, value)),
                "nwexportrevit_section_extract" => ("scope", EnumValue(Scopes, id, value)),
                "nwexportrevit_coordinates" => ("coordinates", EnumValue(Coordinates, id, value)),
                "nwexportrevit_element_ids" => ("export_element_ids", value),
                "nwexportrevit_element_find_missing_materials" => ("find_missing_materials", value),
                "nwexportrevit_element_generic_properties" => ("convert_element_properties", value),
                "nwexportrevit_urls" => ("export_urls", value),
                "nwexportrevit_room" => ("export_room_as_attribute", value),
                "nwexportrevit_room_geometry" => ("export_room_geometry", value),
                "nwexportrevit_linked_files" => ("export_links", value),
                "nwexportrevit_construction_parts" => ("export_parts", value),
                "nwexportrevit_divide_file_into_levels" => ("divide_file_into_levels", value),
                "nwexportrevit_param_faceting_factor" => ("faceting_factor", value),
                "nwexportrevit_linked_CAD_formats" => ("convert_linked_cad_formats", value),
                "nwexportrevit_lights" => ("convert_lights", value),
                _ => (null, value)
            };
            if (mapped.Item1 is not null)
            {
                values[mapped.Item1] = mapped.Item2;
                mapping[id] = mapped.Item1;
            }
            else if (key is "nwexportrevit_embed_textures" or "nwexportrevit_with_type_props"
                     or "nwexportrevit_separate_custom_props" or "nwexportrevit_strict_sectioning")
                notApplied.Add(new NwcUnappliedOption(id, value, "no Revit API property"));
            else
                ignored.Add(new NwcIgnoredOption(id, value));
        }

        return new NwcSettingsResult(values, mapping, notApplied, ignored);
    }

    private static object ReadValue(XElement data)
    {
        var value = data.Value.Trim();
        return (string?)data.Attribute("type") switch
        {
            "bool" when value is "true" or "1" => true,
            "bool" when value is "false" or "0" => false,
            "int32" => int.Parse(value, CultureInfo.InvariantCulture),
            "float" => double.Parse(value, CultureInfo.InvariantCulture),
            "wstring" => value,
            _ => throw new FormatException("Unsupported NWC settings XML data type or value.")
        };
    }

    private static string EnumValue(IReadOnlyList<string> choices, string id, object value)
    {
        var suffix = id.LastIndexOf(':') is var index && index >= 0
            ? id.Substring(index + 1)
            : Convert.ToString(value, CultureInfo.InvariantCulture);
        if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            || ordinal < 0 || ordinal >= choices.Count)
            throw new FormatException($"Invalid NWC exporter option: {id}.");
        return choices[ordinal];
    }
}
