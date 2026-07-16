using Tooltail.Application.Windows;

namespace Tooltail.Application.Abstractions;

public interface IPhysicalPointerSource
{
    bool TryGetCurrentPhysicalPosition(out PhysicalScreenPoint position);
}
