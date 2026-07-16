namespace Tooltail.Platform.Windows.Windowing;

public static class AmbientWindowMessagePolicy
{
    public const int WindowMessageMouseActivate = 0x0021;
    public const int WindowMessageNonClientHitTest = 0x0084;
    public const int MouseActivateNoActivate = 3;
    public const int HitTestTransparent = -1;

    public static bool TryHandlePetMessage(
        int message,
        bool hitsVisibleSprite,
        out nint result)
    {
        switch (message)
        {
            case WindowMessageMouseActivate:
                result = MouseActivateNoActivate;
                return true;
            case WindowMessageNonClientHitTest when !hitsVisibleSprite:
                result = HitTestTransparent;
                return true;
            default:
                result = nint.Zero;
                return false;
        }
    }
}
