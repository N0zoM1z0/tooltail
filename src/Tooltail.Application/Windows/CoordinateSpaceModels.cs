namespace Tooltail.Application.Windows;

public readonly record struct DeviceIndependentPoint(double X, double Y);

public readonly record struct DeviceIndependentRectangle
{
    public DeviceIndependentRectangle(double left, double top, double right, double bottom)
    {
        if (!double.IsFinite(left) ||
            !double.IsFinite(top) ||
            !double.IsFinite(right) ||
            !double.IsFinite(bottom))
        {
            throw new ArgumentOutOfRangeException(nameof(right));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(right, left);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bottom, top);
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public double Left { get; }

    public double Top { get; }

    public double Right { get; }

    public double Bottom { get; }

    public double Width => Right - Left;

    public double Height => Bottom - Top;
}

public readonly record struct MonitorCoordinateReference
{
    public MonitorCoordinateReference(
        PhysicalScreenPoint physicalOrigin,
        DeviceIndependentPoint deviceIndependentOrigin,
        uint dpiX,
        uint dpiY)
    {
        ArgumentOutOfRangeException.ThrowIfZero(dpiX);
        ArgumentOutOfRangeException.ThrowIfZero(dpiY);
        if (!double.IsFinite(deviceIndependentOrigin.X) ||
            !double.IsFinite(deviceIndependentOrigin.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(deviceIndependentOrigin));
        }

        PhysicalOrigin = physicalOrigin;
        DeviceIndependentOrigin = deviceIndependentOrigin;
        DpiX = dpiX;
        DpiY = dpiY;
    }

    public PhysicalScreenPoint PhysicalOrigin { get; }

    public DeviceIndependentPoint DeviceIndependentOrigin { get; }

    public uint DpiX { get; }

    public uint DpiY { get; }
}
