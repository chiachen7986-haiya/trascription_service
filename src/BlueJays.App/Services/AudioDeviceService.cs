using BlueJays.Core.Models;
using NAudio.CoreAudioApi;

namespace BlueJays.App.Services;

public sealed record AudioDevice(string Id, string Name, AudioSourceKind Kind);

public static class AudioDeviceService
{
    public static bool IsAvailable(AudioSourceSettings source)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator(); using var endpoint = enumerator.GetDevice(source.DeviceId);
            return endpoint.State == DeviceState.Active && endpoint.DataFlow == (source.Kind == AudioSourceKind.Microphone ? DataFlow.Capture : DataFlow.Render);
        }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    public static IReadOnlyList<AudioDevice> List(AudioSourceKind kind)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(
            kind == AudioSourceKind.Microphone ? DataFlow.Capture : DataFlow.Render, DeviceState.Active);
        var result = new List<AudioDevice>();
        foreach (var device in devices)
        {
            using (device) result.Add(new(device.ID, device.FriendlyName, kind));
        }
        return result;
    }
}
