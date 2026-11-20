namespace RevitModelMcp.Core.Export;

public static class IfcFileValidation
{
    public static void RequireBuildingElements(TextReader reader, bool scopeHasModelElements)
    {
        if (reader is null) throw new ArgumentNullException(nameof(reader));
        if (!scopeHasModelElements) return;
        var token = new char[64];
        var length = 0;
        var state = 0;
        var quoted = false;
        var comment = false;
        var previous = '\0';
        int value;
        while ((value = reader.Read()) != -1)
        {
            var character = (char)value;
            if (comment)
            {
                if (previous == '*' && character == '/') { comment = false; character = ' '; }
                previous = character;
                continue;
            }
            if (!quoted && character == '/' && reader.Peek() == '*')
            {
                reader.Read();
                comment = true;
                previous = '\0';
                continue;
            }
            if (character == '\'') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (character == '#') { state = 1; length = 0; continue; }
            if (state == 1 && character is >= '0' and <= '9') { state = 2; continue; }
            if (state == 2 && character is >= '0' and <= '9') continue;
            if (state == 2 && character == '=') { state = 3; continue; }
            if (state == 3 && character is >= 'A' and <= 'Z')
            {
                if (length == token.Length) { state = 0; continue; }
                token[length++] = character;
                continue;
            }
            if (state == 3 && length > 0 && character is >= '0' and <= '9')
            {
                if (length == token.Length) { state = 0; continue; }
                token[length++] = character;
                continue;
            }
            if (state == 3 && length > 0 && character == '(' && IsPhysicalElement(token, length)) return;
            if (!char.IsWhiteSpace(character)) state = 0;
        }
        throw new InvalidOperationException("IFC export contains no building elements although the export scope contains model elements. Check the IFC exporter installation, view visibility and phase; the file is not a successful model export.");
    }

    private static bool IsPhysicalElement(char[] entity, int length)
    {
        foreach (var name in PhysicalElements)
        {
            if (name.Length != length) continue;
            var index = 0;
            while (index < length && name[index] == entity[index]) index++;
            if (index == length) return true;
        }
        return false;
    }

    private static readonly string[] PhysicalElements =
    [
        "IFCBEAM", "IFCBEAMSTANDARDCASE", "IFCBUILDINGELEMENTPROXY", "IFCBUILDINGELEMENTPART",
        "IFCCHIMNEY", "IFCCOLUMN", "IFCCOLUMNSTANDARDCASE", "IFCCOVERING", "IFCCURTAINWALL",
        "IFCDOOR", "IFCDOORSTANDARDCASE", "IFCFOOTING", "IFCMEMBER", "IFCMEMBERSTANDARDCASE",
        "IFCPILE", "IFCPLATE", "IFCPLATESTANDARDCASE", "IFCRAILING", "IFCRAMP", "IFCRAMPFLIGHT",
        "IFCROOF", "IFCSHADINGDEVICE", "IFCSLAB", "IFCSLABSTANDARDCASE", "IFCSLABELEMENTEDCASE",
        "IFCSTAIR", "IFCSTAIRFLIGHT", "IFCWALL", "IFCWALLSTANDARDCASE", "IFCWALLELEMENTEDCASE",
        "IFCWINDOW", "IFCWINDOWSTANDARDCASE", "IFCFURNISHINGELEMENT", "IFCFURNITURE",
        "IFCSYSTEMFURNITUREELEMENT", "IFCELEMENTASSEMBLY", "IFCGEOGRAPHICELEMENT", "IFCCIVILELEMENT",
        "IFCFASTENER", "IFCMECHANICALFASTENER", "IFCREINFORCINGBAR", "IFCREINFORCINGMESH",
        "IFCTENDON", "IFCTENDONANCHOR", "IFCDISCRETEACCESSORY", "IFCVIBRATIONISOLATOR",
        "IFCDISTRIBUTIONELEMENT", "IFCDISTRIBUTIONFLOWELEMENT", "IFCFLOWSEGMENT", "IFCFLOWFITTING",
        "IFCFLOWTERMINAL", "IFCFLOWCONTROLLER", "IFCFLOWMOVINGDEVICE", "IFCFLOWSTORAGEDEVICE",
        "IFCFLOWTREATMENTDEVICE", "IFCENERGYCONVERSIONDEVICE", "IFCDISTRIBUTIONCONTROLELEMENT",
        "IFCACTUATOR", "IFCAIRTERMINAL", "IFCAIRTERMINALBOX", "IFCAIRTOAIRHEATRECOVERY",
        "IFCALARM", "IFCAUDIOVISUALAPPLIANCE", "IFCBOILER", "IFCBURNER", "IFCCABLECARRIERFITTING",
        "IFCCABLECARRIERSEGMENT", "IFCCABLEFITTING", "IFCCABLESEGMENT", "IFCCHILLER", "IFCCOIL",
        "IFCCOMMUNICATIONSAPPLIANCE", "IFCCOMPRESSOR", "IFCCONDENSER", "IFCCONTROLLER",
        "IFCCOOLEDBEAM", "IFCCOOLINGTOWER", "IFCDAMPER", "IFCDISTRIBUTIONCHAMBERELEMENT",
        "IFCDUCTFITTING", "IFCDUCTSEGMENT", "IFCDUCTSILENCER", "IFCELECTRICAPPLIANCE",
        "IFCELECTRICDISTRIBUTIONBOARD", "IFCELECTRICDISTRIBUTIONPOINT", "IFCELECTRICFLOWSTORAGEDEVICE",
        "IFCELECTRICGENERATOR", "IFCELECTRICMOTOR", "IFCELECTRICTIMECONTROL", "IFCEVAPORATIVECOOLER",
        "IFCEVAPORATOR", "IFCFAN", "IFCFILTER", "IFCFIRESUPPRESSIONTERMINAL", "IFCFLOWINSTRUMENT",
        "IFCHEATEXCHANGER", "IFCHUMIDIFIER", "IFCINTERCEPTOR", "IFCJUNCTIONBOX", "IFCLAMP",
        "IFCLIGHTFIXTURE", "IFCMEDICALDEVICE", "IFCMOTORCONNECTION", "IFCOUTLET", "IFCPIPEFITTING",
        "IFCPIPESEGMENT", "IFCPROTECTIVEDEVICE", "IFCPROTECTIVEDEVICETRIPPINGUNIT", "IFCPUMP",
        "IFCSANITARYTERMINAL", "IFCSENSOR", "IFCSOLARDEVICE", "IFCSPACEHEATER", "IFCSTACKTERMINAL",
        "IFCSWITCHINGDEVICE", "IFCTANK", "IFCTRANSFORMER", "IFCTRANSPORTELEMENT",
        "IFCTUBEBUNDLE", "IFCUNITARYCONTROLELEMENT", "IFCUNITARYEQUIPMENT", "IFCVALVE",
        "IFCWASTETERMINAL", "IFCBUILTELEMENT", "IFCBUILDINGELEMENT", "IFCBEARING", "IFCCOURSE",
        "IFCDEEPFOUNDATION", "IFCEARTHWORKSCUT", "IFCEARTHWORKSFILL", "IFCKERB", "IFCMOORINGDEVICE",
        "IFCNAVIGATIONELEMENT", "IFCPAVEMENT", "IFCRAIL", "IFCRAILWAYTRACKELEMENT",
        "IFCSIGN", "IFCSIGNAL", "IFCTRACKELEMENT"
    ];
}
