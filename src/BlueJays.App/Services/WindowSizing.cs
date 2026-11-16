using System.Windows;

namespace BlueJays.App.Services;

internal static class WindowSizing
{
    public static void FitToWorkArea(Window window)
    {
        // Respect laptop displays and Windows scaling on the initial launch.
        var area = SystemParameters.WorkArea;
        window.MinWidth = Math.Min(window.MinWidth, Math.Max(320, area.Width - 24));
        window.MinHeight = Math.Min(window.MinHeight, Math.Max(240, area.Height - 24));
        window.Width = Math.Min(window.Width, area.Width - 24);
        window.Height = Math.Min(window.Height, area.Height - 24);
    }
}
