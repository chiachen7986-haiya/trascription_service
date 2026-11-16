using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Data;
using BlueJays.App.Services;
using L = BlueJays.App.Localization.Localizer;

namespace BlueJays.App.Views;

public partial class RoomWindow : Window
{
    private readonly FullScreenController fullScreen;
    public RoomWindow() { InitializeComponent(); WindowSizing.FitToWorkArea(this); fullScreen = new(this); }
    private void FullScreen_Click(object sender, RoutedEventArgs e) => Toggle();
    private void Toggle()
    {
        fullScreen.Toggle();
        var key = L.MakeKey(fullScreen.IsFullScreen ? "Exit full screen · Esc" : "Full screen · F11");
        FullScreenButton.SetBinding(ContentControl.ContentProperty, new Binding($"[{key}]") { Source = L.Current, Mode = BindingMode.OneWay });
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 || e.Key == Key.Escape && fullScreen.IsFullScreen) { Toggle(); e.Handled = true; }
    }
}
