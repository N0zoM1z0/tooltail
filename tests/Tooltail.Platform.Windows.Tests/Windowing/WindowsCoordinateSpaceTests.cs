using Tooltail.Application.Windows;
using Tooltail.Platform.Windows.Windowing;

namespace Tooltail.Platform.Windows.Tests.Windowing;

public sealed class WindowsCoordinateSpaceTests
{
    [Theory]
    [InlineData(96u)]
    [InlineData(120u)]
    [InlineData(144u)]
    [InlineData(192u)]
    public void ConvertsCommonMonitorScalesWithoutMixingPixelsAndDips(uint dpi)
    {
        WindowsCoordinateSpace coordinates = new();
        MonitorCoordinateReference reference = new(
            new PhysicalScreenPoint(0, 0),
            new DeviceIndependentPoint(0, 0),
            dpi,
            dpi);
        int physicalExtent = checked((int)dpi);

        DeviceIndependentRectangle converted = coordinates.ToDeviceIndependent(
            new PhysicalScreenRectangle(0, 0, physicalExtent, physicalExtent),
            reference);

        Assert.Equal(96d, converted.Width, precision: 8);
        Assert.Equal(96d, converted.Height, precision: 8);
        Assert.Equal(
            new PhysicalScreenPoint(physicalExtent, physicalExtent),
            coordinates.ToPhysical(
                new DeviceIndependentPoint(converted.Right, converted.Bottom),
                reference));
    }

    [Fact]
    public void PreservesNegativeMonitorOriginsThroughRoundTrip()
    {
        WindowsCoordinateSpace coordinates = new();
        MonitorCoordinateReference reference = new(
            new PhysicalScreenPoint(-1920, -200),
            new DeviceIndependentPoint(-1536, -160),
            dpiX: 120,
            dpiY: 120);
        PhysicalScreenPoint physical = new(-1795, -75);

        DeviceIndependentPoint dip = coordinates.ToDeviceIndependent(physical, reference);

        Assert.Equal(new DeviceIndependentPoint(-1436, -60), dip);
        Assert.Equal(physical, coordinates.ToPhysical(dip, reference));
    }

    [Fact]
    public void SupportsIndependentAxesForRotatedMonitorGeometry()
    {
        WindowsCoordinateSpace coordinates = new();
        MonitorCoordinateReference reference = new(
            new PhysicalScreenPoint(2560, -1080),
            new DeviceIndependentPoint(2048, -864),
            dpiX: 144,
            dpiY: 192);

        DeviceIndependentPoint dip = coordinates.ToDeviceIndependent(
            new PhysicalScreenPoint(2704, -888),
            reference);

        Assert.Equal(new DeviceIndependentPoint(2144, -768), dip);
        Assert.Equal(
            new PhysicalScreenPoint(2704, -888),
            coordinates.ToPhysical(dip, reference));
    }
}
