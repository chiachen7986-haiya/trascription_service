using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BlueJays.App.Services;

/// <summary>Uses the monitor containing this window, including monitors left of the primary display.</summary>
internal sealed class FullScreenController(Window window)
{
    private Rect restore;
    public bool IsFullScreen { get; private set; }
    public void Toggle()
    {
        if (IsFullScreen)
        {
            window.WindowStyle = WindowStyle.SingleBorderWindow; window.ResizeMode = ResizeMode.CanResize;
            window.Left = restore.Left; window.Top = restore.Top; window.Width = restore.Width; window.Height = restore.Height;
            IsFullScreen = false; return;
        }
        window.WindowState = WindowState.Normal;
        restore = new(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
        var monitor = MonitorFromWindow(new WindowInteropHelper(window).Handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        window.WindowStyle = WindowStyle.None; window.ResizeMode = ResizeMode.NoResize;
        window.Left = info.Monitor.Left / dpi.DpiScaleX; window.Top = info.Monitor.Top / dpi.DpiScaleY;
        window.Width = (info.Monitor.Right - info.Monitor.Left) / dpi.DpiScaleX;
        window.Height = (info.Monitor.Bottom - info.Monitor.Top) / dpi.DpiScaleY;
        IsFullScreen = true;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
