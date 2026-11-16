using System.IO;
using System.Text.Json;
using BlueJays.App.Services;
using BlueJays.Core.Models;
using BlueJays.Core.Conversation;
using NAudio.CoreAudioApi;

internal static class AudioDiagnostics
{
    public static async Task<int> StartSessionAsync()
    {
        // Uses the real saved model/sources but no output languages, API key or export.
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(SettingsStore.DataDirectory, "settings.json")));
        var settings = json.RootElement.GetProperty("Settings").Deserialize<AppSettings>()!;
        var listening = new List<string>();
        await using var session = new InterpretationSession(settings, "", new ConversationOptions { SpokenLanguages = ["ja"], TargetLanguages = [] }, new ConversationMemory());
        session.Warning += Console.WriteLine;
        session.SourceStateChanged += (id, state) => { Console.WriteLine($"{id}: {state}"); if (state == "Listening") listening.Add(id); };
        await session.StartAsync();
        await Task.Delay(150);
        if (listening.Count == 0) throw new Exception("No source reached Listening.");
        Console.WriteLine($"PASS actual saved configuration started with {listening.Count} available sources; no API calls or audio/transcript files.");
        return 0;
    }

    // Opens saved endpoint clients only. Does not start capture, load STT or call an API.
    public static int Run()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(SettingsStore.DataDirectory, "settings.json")));
        var settings = json.RootElement.GetProperty("Settings").Deserialize<AppSettings>()!;
        using var enumerator = new MMDeviceEnumerator();
        foreach (var source in settings.Sources.Where(s => s.Enabled))
        {
            Console.WriteLine($"Source: {source.Name} ({source.Kind})");
            try
            {
                using var endpoint = enumerator.GetDevice(source.DeviceId);
                Console.WriteLine($"  Windows state: {endpoint.State}; flow: {endpoint.DataFlow}");
                using var capture = new AudioCaptureSource(source, settings);
                Console.WriteLine("  Capture client opened successfully (recording not started).");
            }
            catch (Exception ex) { Console.WriteLine($"  {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}\n{ex.StackTrace}"); }
        }
        return 0;
    }
}
