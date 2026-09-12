using RevitModelMcp.Core.Units;

namespace RevitModelMcp.Core.Tests.Units;

public sealed class UnitConverterTests
{
    [Test]
    public async Task FeetToMillimeters_ConvertsRevitInternalLength()
    {
        await Assert.That(UnitConverter.FeetToMillimeters(1)).IsEqualTo(304.8);
    }

    [Test]
    public async Task SquareFeetToSquareMeters_ConvertsRevitInternalArea()
    {
        await Assert.That(UnitConverter.SquareFeetToSquareMeters(1)).IsEqualTo(0.092903);
    }

    [Test]
    public async Task CubicFeetToCubicMeters_ConvertsRevitInternalVolume()
    {
        await Assert.That(UnitConverter.CubicFeetToCubicMeters(1)).IsEqualTo(0.028317);
    }
}
