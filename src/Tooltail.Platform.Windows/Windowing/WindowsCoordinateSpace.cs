using Tooltail.Application.Abstractions;
using Tooltail.Application.Windows;

namespace Tooltail.Platform.Windows.Windowing;

public sealed class WindowsCoordinateSpace : ICoordinateSpace
{
    private const double DeviceIndependentDpi = 96d;

    public DeviceIndependentPoint ToDeviceIndependent(
        PhysicalScreenPoint physicalPoint,
        MonitorCoordinateReference reference) =>
        new(
            reference.DeviceIndependentOrigin.X + ScaleToDip(
                physicalPoint.X - (double)reference.PhysicalOrigin.X,
                reference.DpiX),
            reference.DeviceIndependentOrigin.Y + ScaleToDip(
                physicalPoint.Y - (double)reference.PhysicalOrigin.Y,
                reference.DpiY));

    public DeviceIndependentRectangle ToDeviceIndependent(
        PhysicalScreenRectangle physicalRectangle,
        MonitorCoordinateReference reference)
    {
        DeviceIndependentPoint topLeft = ToDeviceIndependent(
            new PhysicalScreenPoint(physicalRectangle.Left, physicalRectangle.Top),
            reference);
        DeviceIndependentPoint bottomRight = ToDeviceIndependent(
            new PhysicalScreenPoint(physicalRectangle.Right, physicalRectangle.Bottom),
            reference);
        return new DeviceIndependentRectangle(
            topLeft.X,
            topLeft.Y,
            bottomRight.X,
            bottomRight.Y);
    }

    public PhysicalScreenPoint ToPhysical(
        DeviceIndependentPoint deviceIndependentPoint,
        MonitorCoordinateReference reference)
    {
        if (!double.IsFinite(deviceIndependentPoint.X) ||
            !double.IsFinite(deviceIndependentPoint.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(deviceIndependentPoint));
        }

        double physicalX = reference.PhysicalOrigin.X + ScaleToPhysical(
            deviceIndependentPoint.X - reference.DeviceIndependentOrigin.X,
            reference.DpiX);
        double physicalY = reference.PhysicalOrigin.Y + ScaleToPhysical(
            deviceIndependentPoint.Y - reference.DeviceIndependentOrigin.Y,
            reference.DpiY);
        return new PhysicalScreenPoint(
            checked((int)Math.Round(physicalX, MidpointRounding.AwayFromZero)),
            checked((int)Math.Round(physicalY, MidpointRounding.AwayFromZero)));
    }

    private static double ScaleToDip(double physical, uint dpi) =>
        physical * DeviceIndependentDpi / dpi;

    private static double ScaleToPhysical(double dip, uint dpi) =>
        dip * dpi / DeviceIndependentDpi;
}
