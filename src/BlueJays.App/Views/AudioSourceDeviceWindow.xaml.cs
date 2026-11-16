using System.Windows;
using BlueJays.App.Services;
using BlueJays.Core.Models;
using L = BlueJays.App.Localization.Localizer;

namespace BlueJays.App.Views;

public partial class AudioSourceDeviceWindow : Window
{
    private readonly AudioSourceSettings source;
    public AudioDevice? Result { get; private set; }
    public AudioSourceDeviceWindow(AudioSourceSettings source, IReadOnlyList<AudioDevice>? devices = null)
    {
        this.source = source; InitializeComponent(); WindowSizing.FitToWorkArea(this);
        SourceNameText.Text = source.Name;
        if (devices is not null) SetDevices(devices); else RefreshDevices();
    }
    private void SetDevices(IReadOnlyList<AudioDevice> devices)
    {
        DeviceBox.ItemsSource = devices; DeviceBox.SelectedValue = source.DeviceId;
        ErrorText.Text = devices.Count == 0 ? L.T("No active device found. Connect a microphone or audio output and press Refresh.") : "";
    }
    private void RefreshDevices()
    {
        try { SetDevices(AudioDeviceService.List(source.Kind)); }
        catch (Exception ex) { ErrorText.Text = L.F("Could not list audio devices: {0}", ex.Message); }
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();
    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not AudioDevice device) { ErrorText.Text = L.T("Choose an active audio device."); return; }
        Result = device; DialogResult = true;
    }
}
