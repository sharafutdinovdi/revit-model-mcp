using RevitModelMcp.Core.Export;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Export;

public sealed class NwcSettingsXmlTests
{
    [Test]
    public async Task Parse_FindsNestedOptionsAndClassifiesUnsupportedIds()
    {
        const string xml = """
            <optionset name="root">
              <optionset name="export">
                <option name="nwexportrevit_element_params:1"><data type="int32">1</data></option>
                <option name="nwexportrevit_section_extract:2"><data type="int32">2</data></option>
                <option name="nwexportrevit_coordinates:0"><data type="int32">0</data></option>
                <option name="nwexportrevit_element_ids"><data type="bool">false</data></option>
                <option name="nwexportrevit_param_faceting_factor"><data type="float">2.5</data></option>
                <option name="nwexportrevit_embed_textures"><data type="bool">true</data></option>
                <option name="other_option"><data type="wstring">hello</data></option>
              </optionset>
            </optionset>
            """;

        var result = NwcSettingsXml.Parse(xml);

        await Assert.That(result.Values["parameters"]).IsEqualTo("elements");
        await Assert.That(result.Values["scope"]).IsEqualTo("selection");
        await Assert.That(result.Values["coordinates"]).IsEqualTo("shared");
        await Assert.That((bool)result.Values["export_element_ids"]).IsFalse();
        await Assert.That(result.Values["faceting_factor"]).IsEqualTo(2.5);
        await Assert.That(result.NotApplied.Single().Id).IsEqualTo("nwexportrevit_embed_textures");
        await Assert.That(result.NotApplied.Single().Reason).IsEqualTo("no Revit API property");
        await Assert.That(result.Ignored.Single().Id).IsEqualTo("other_option");
    }

    [Test]
    public async Task Parse_AndSerializeReadResult()
    {
        const string xml = """<optionset><option name="nwexportrevit_room"><data type="bool">true</data></option><option name="nwexportrevit_with_type_props"><data type="bool">false</data></option></optionset>""";
        var result = NwcSettingsXml.Parse(xml);
        var json = CommandResponseJsonSerializer.Serialize(CommandResponse<NwcSettingsResult>.Ok("nwc-settings-check", result, 1));
        await Assert.That(json).Contains("export_room_as_attribute");
        await Assert.That(json).Contains("no Revit API property");
    }

    [Test]
    public async Task Parse_ReadToolAndExportPreserveXmlPathAndExplicitOptions()
    {
        var read = ControlJobParser.Parse("""{"command":"nwc-settings-check","settingsXml":"C:\\x\\settings.xml"}""");
        await Assert.That(read.Kind).IsEqualTo(ControlJobKind.NwcSettingsCheck);
        await Assert.That(read.CoordinatorJob.SettingsXml).IsEqualTo(@"C:\x\settings.xml");
        var export = ControlJobParser.Parse("""{"command":"export-nwc","path":"C:\\x\\a.nwc","settingsXml":"C:\\x\\settings.xml","exportLinks":false}""");
        await Assert.That(export.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(export.Action!.Nwc.ExplicitOptions.Contains("export_links")).IsTrue();
        await Assert.That(export.Action.Nwc.ExplicitOptions.Contains("parameters")).IsFalse();
    }

    [Test]
    public async Task Parse_UsesEnabledEnumChoice()
    {
        const string xml = """<optionset><option name="nwexportrevit_element_params:0"><data type="bool">false</data></option><option name="nwexportrevit_element_params:2"><data type="bool">true</data></option></optionset>""";
        var result = NwcSettingsXml.Parse(xml);
        await Assert.That(result.Values["parameters"]).IsEqualTo("all");
        await Assert.That(result.Mapping.ContainsKey("nwexportrevit_element_params:0")).IsFalse();
    }

    [Test]
    public async Task Parse_RejectsDtd()
    {
        const string xml = "<!DOCTYPE optionset [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><optionset>&x;</optionset>";
        await Assert.That(() => NwcSettingsXml.Parse(xml)).Throws<System.Xml.XmlException>();
    }

    [Test]
    public async Task ReadFile_RejectsDevicePathTraversalAndNonXmlExtension()
    {
        await Assert.That(() => NwcSettingsXml.ReadFile(@"\\?\C:\settings.xml")).Throws<ArgumentException>();
        await Assert.That(() => NwcSettingsXml.ReadFile(@"\\.\C:\settings.xml")).Throws<ArgumentException>();
        await Assert.That(() => NwcSettingsXml.ReadFile(@"C:\a\..\settings.xml")).Throws<ArgumentException>();
        await Assert.That(() => NwcSettingsXml.ReadFile("settings.xml")).Throws<ArgumentException>();
        await Assert.That(() => NwcSettingsXml.ReadFile(@"C:\x\settings.txt")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Parse_WstringSelfReferencingEnumValue_ResolvesOrdinalFromValue()
    {
        const string xml = """
            <optionset><option name="nwexportrevit_element_params"><data type="wstring">nwexportrevit_element_params:1</data></option></optionset>
            """;
        var result = NwcSettingsXml.Parse(xml);
        await Assert.That(result.Values["parameters"]).IsEqualTo("elements");
        await Assert.That(result.Invalid).IsEmpty();
    }

    [Test]
    public async Task Parse_UnrecognizedEnumOrdinal_GoesToInvalidWithoutAbortingFile()
    {
        const string xml = """
            <optionset>
              <option name="nwexportrevit_element_params"><data type="wstring">nwexportrevit_element_params:99</data></option>
              <option name="nwexportrevit_room"><data type="bool">true</data></option>
            </optionset>
            """;
        var result = NwcSettingsXml.Parse(xml);
        await Assert.That(result.Values.ContainsKey("parameters")).IsFalse();
        await Assert.That((bool)result.Values["export_room_as_attribute"]).IsTrue();
        await Assert.That(result.Invalid.Single().Id).IsEqualTo("nwexportrevit_element_params");
        await Assert.That(result.Invalid.Single().Reason).IsEqualTo("unrecognized enum value for parameters");
    }

    [Test]
    public async Task Parse_MalformedDataValue_GoesToInvalidWithoutAbortingFile()
    {
        const string xml = """
            <optionset>
              <option name="nwexportrevit_element_ids"><data type="int32">not-a-number</data></option>
              <option name="nwexportrevit_room"><data type="bool">true</data></option>
            </optionset>
            """;
        var result = NwcSettingsXml.Parse(xml);
        await Assert.That((bool)result.Values["export_room_as_attribute"]).IsTrue();
        await Assert.That(result.Invalid.Single().Id).IsEqualTo("nwexportrevit_element_ids");
        await Assert.That(result.Invalid.Single().Value).IsEqualTo("not-a-number");
    }
}
