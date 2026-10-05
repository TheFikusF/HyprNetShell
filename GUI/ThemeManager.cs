namespace HyprNetShell.GUI;

public static class ThemeManager
{
    private static Theme _current = Theme.CreateDefault();

    public static Theme Current
    {
        get => _current;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _current = value;
        }
    }
}
