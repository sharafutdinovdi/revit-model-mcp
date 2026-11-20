using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Export;

namespace RevitModelMcp.Core.Tests.Export;

public sealed class IfcFileValidationTests
{
    [Test]
    [Arguments(IfcExportElementKind.Link)]
    [Arguments(IfcExportElementKind.Import)]
    [Arguments(IfcExportElementKind.Spatial)]
    [Arguments(IfcExportElementKind.Curve)]
    [Arguments(IfcExportElementKind.ReferencePoint)]
    [Arguments(IfcExportElementKind.Terrain)]
    [Arguments(IfcExportElementKind.Construction)]
    [Arguments(IfcExportElementKind.PointCloud)]
    [Arguments(IfcExportElementKind.Container)]
    public async Task NonPhysicalScopesPermitMetadataOnlyOutput(IfcExportElementKind kind)
    {
        var scopeHasPhysicalElements = IfcFileValidation.IsPhysicalModelElement(kind, true, false);
        using var reader = new StringReader("#1=IFCPROJECT('project');");
        IfcFileValidation.RequireBuildingElements(reader, scopeHasPhysicalElements);
        await Assert.That(scopeHasPhysicalElements).IsFalse();
        await Assert.That(reader.Read()).IsEqualTo((int)'#');
    }

    [Test]
    public async Task RoomsAndLinesOnlyPermitMetadataOnlyOutput()
    {
        var scope = new[] { IfcExportElementKind.Spatial, IfcExportElementKind.Curve };
        var scopeHasPhysicalElements = scope.Any(kind => IfcFileValidation.IsPhysicalModelElement(kind, true, false));
        using var reader = new StringReader("#1=IFCPROJECT('project');");
        IfcFileValidation.RequireBuildingElements(reader, scopeHasPhysicalElements);
        await Assert.That(scopeHasPhysicalElements).IsFalse();
    }

    [Test]
    public async Task OneWallRequiresPhysicalIfcOutputEvenAlongsideReferences()
    {
        var scope = new[] { IfcExportElementKind.Link, IfcExportElementKind.ReferencePoint, IfcExportElementKind.Physical };
        var scopeHasPhysicalElements = scope.Any(kind => IfcFileValidation.IsPhysicalModelElement(kind, true, false));
        using var reader = new StringReader("#1=IFCPROJECT('project');");
        Exception? failure = null;
        try { IfcFileValidation.RequireBuildingElements(reader, scopeHasPhysicalElements); }
        catch (InvalidOperationException exception) { failure = exception; }
        await Assert.That(scopeHasPhysicalElements).IsTrue();
        await Assert.That(failure?.Message).Contains("no building elements");
    }

    [Test]
    [Arguments(true, true)]
    [Arguments(false, false)]
    [Arguments(false, true)]
    public async Task ViewSpecificAndNonModelElementsDoNotRequirePhysicalOutput(bool isModelCategory, bool isViewSpecific)
    {
        await Assert.That(IfcFileValidation.IsPhysicalModelElement(
            IfcExportElementKind.Physical, isModelCategory, isViewSpecific)).IsFalse();
    }

    [Test]
    [Arguments("IFCWALL")]
    [Arguments("IFCWALLSTANDARDCASE")]
    [Arguments("IFCSLAB")]
    [Arguments("IFCBEAM")]
    [Arguments("IFCBUILDINGELEMENTPROXY")]
    [Arguments("IFCFURNISHINGELEMENT")]
    [Arguments("IFCDUCTSEGMENT")]
    [Arguments("IFCPIPEFITTING")]
    [Arguments("IFCREINFORCINGBAR")]
    [Arguments("IFCTRACKELEMENT")]
    public async Task PhysicalElementPassesWithoutRequiringWalls(string entity)
    {
        using var reader = new StringReader($"ISO-10303-21;\nDATA;\n#123 =\n{entity} ( 'id', $ );\nENDSEC;");
        IfcFileValidation.RequireBuildingElements(reader, true);
        await Assert.That(reader.Peek()).IsNotEqualTo(-1);
    }

    [Test]
    [Arguments("")]
    [Arguments("#1=IFCPROJECT('project');#2=IFCSITE('site');#3=IFCBUILDING('building');#4=IFCBUILDINGSTOREY('level');")]
    [Arguments("#1=IFCPROPERTYSET('IFCWALL', '#12=IFCSLAB()');")]
    [Arguments("/* #12=IFCWALL(); */ #1=IFCWALLTYPE();")]
    [Arguments("#1=IFCPROPERTYSINGLEVALUE('escaped '' #12=IFCWALL();', $);#2=IFCOPENINGELEMENT();")]
    [Arguments("#1=IFCCARTESIANPOINT((1.,2.,3.));#2=IFCWALL;#3=IFCWALLSTANDARDCASETYPE();")]
    public async Task MetadataTypesGeometryStringsAndCommentsDoNotPass(string content)
    {
        using var reader = new StringReader(content);
        Exception? failure = null;
        try { IfcFileValidation.RequireBuildingElements(reader, true); }
        catch (InvalidOperationException exception) { failure = exception; }
        await Assert.That(failure?.Message).Contains("no building elements");
        await Assert.That(failure?.Message).Contains("export scope contains model elements");
    }

    [Test]
    public async Task EmptyScopeDoesNotRequireAnElementOrReadTheFile()
    {
        using var reader = new StringReader("metadata");
        IfcFileValidation.RequireBuildingElements(reader, false);
        await Assert.That(reader.Read()).IsEqualTo((int)'m');
    }

    [Test]
    public async Task StreamsLongRecordsWithoutReadingWholeLines()
    {
        using var reader = new LongRecordReader(8 * 1024 * 1024);
        IfcFileValidation.RequireBuildingElements(reader, true);
        await Assert.That(reader.Remaining).IsEqualTo(0);
    }

    [Test]
    [Arguments(2022, "IFC2x3CV2")]
    [Arguments(2022, "IFC4RV")]
    [Arguments(2023, "IFC4x3")]
    [Arguments(2024, "IFC4x3")]
    [Arguments(2025, "IFC4x3")]
    [Arguments(2026, "IFC4x3")]
    [Arguments(2027, "IFC4x3")]
    public async Task SupportedYearVersionsPass(int year, string version)
    {
        var job = new FileExportJob { Format = "ifc", Options = new FileExportOptions { Version = version } };
        job.Validate();
        job.Options.ValidateIfcVersion(year.ToString(), year == 2022 ? ["IFC2x3CV2", "IFC4RV"] : ["IFC2x3CV2", "IFC4RV", "IFC4x3"]);
        await Assert.That(job.Options.Version).IsEqualTo(version);
    }

    [Test]
    [Arguments("2022")]
    [Arguments("2023")]
    public async Task OlderApiBuildRefusesIfc43WithSupportedValues(string year)
    {
        var options = new FileExportOptions { Version = "IFC4x3" };
        Exception? failure = null;
        try { options.ValidateIfcVersion(year, ["IFC2x3CV2", "IFC4RV"]); }
        catch (ArgumentException exception) { failure = exception; }
        await Assert.That(failure?.Message).Contains($"not supported by this Revit {year} build");
        await Assert.That(failure?.Message).Contains("Supported values: IFC2x3CV2, IFC4RV");
    }

    [Test]
    public async Task ForeignFormatOptionsAreRefusedEvenWhenFalse()
    {
        var parsed = ControlJobParser.Parse("""{"command":"export","format":"ifc","options":{"combine":false}}""");
        await Assert.That(parsed.Error).Contains("Unsupported IFC options");
        await Assert.That(parsed.Error).Contains("Supported keys:");
    }

    private sealed class LongRecordReader(int remaining) : TextReader
    {
        private readonly StringReader _prefix = new("#1=IFCPROPERTYSINGLEVALUE('");
        private readonly StringReader _suffix = new("');\n#2 /* comment */ = IFCWALLSTANDARDCASE\n(");
        public int Remaining { get; private set; } = remaining;

        public override int Read()
        {
            var prefix = _prefix.Read();
            if (prefix != -1) return prefix;
            if (Remaining > 0) { Remaining--; return 'x'; }
            return _suffix.Read();
        }

        public override int Peek()
        {
            var prefix = _prefix.Peek();
            return prefix != -1 ? prefix : Remaining > 0 ? 'x' : _suffix.Peek();
        }

        public override string ReadToEnd() => throw new InvalidOperationException("Whole-file reads are forbidden.");
        public override string ReadLine() => throw new InvalidOperationException("Whole-line reads are forbidden.");

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _prefix.Dispose(); _suffix.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
