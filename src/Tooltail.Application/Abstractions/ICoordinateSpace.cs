using Tooltail.Application.Windows;

namespace Tooltail.Application.Abstractions;

/// <summary>
/// Converts explicitly labeled physical-pixel screen coordinates and WPF-style
/// device-independent coordinates through one monitor reference space.
/// </summary>
public interface ICoordinateSpace
{
    DeviceIndependentPoint ToDeviceIndependent(
        PhysicalScreenPoint physicalPoint,
        MonitorCoordinateReference reference);

    DeviceIndependentRectangle ToDeviceIndependent(
        PhysicalScreenRectangle physicalRectangle,
        MonitorCoordinateReference reference);

    PhysicalScreenPoint ToPhysical(
        DeviceIndependentPoint deviceIndependentPoint,
        MonitorCoordinateReference reference);
}
