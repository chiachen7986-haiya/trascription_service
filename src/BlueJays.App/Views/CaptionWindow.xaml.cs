using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlueJays.App.Services;
using BlueJays.App.ViewModels;

namespace BlueJays.App.Views;

public partial class CaptionWindow : Window
{
    public CaptionWindow() { InitializeComponent(); WindowSizing.FitToWorkArea(this); }
    private void Priority_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel model && sender is Button { Tag: string language }) model.PriorityLanguage = language; }
    private void Smaller_Click(object sender, RoutedEventArgs e) => FontSize = Math.Max(18, FontSize - 2);
    private void Larger_Click(object sender, RoutedEventArgs e) => FontSize = Math.Min(42, FontSize + 2);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Drag_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
    private void Scroll_Changed(object sender, ScrollChangedEventArgs e) { if (e.ExtentHeightChange != 0) CaptionScroll.ScrollToEnd(); }
}
