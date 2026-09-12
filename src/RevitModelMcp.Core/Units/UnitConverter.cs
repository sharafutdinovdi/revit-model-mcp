namespace RevitModelMcp.Core.Units;

public static class UnitConverter
{
    private const double MillimetersPerFoot = 304.8;
    private const double SquareMetersPerSquareFoot = 0.09290304;
    private const double CubicMetersPerCubicFoot = 0.028316846592;

    public static double FeetToMillimeters(double value)
    {
        return Math.Round(value * MillimetersPerFoot, 3);
    }

    public static double SquareFeetToSquareMeters(double value)
    {
        return Math.Round(value * SquareMetersPerSquareFoot, 6);
    }

    public static double CubicFeetToCubicMeters(double value)
    {
        return Math.Round(value * CubicMetersPerCubicFoot, 6);
    }
}
