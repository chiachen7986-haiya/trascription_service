using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueJays.Core.Models;

namespace BlueJays.App.Services;

public sealed class SettingsStore(string? dataDirectory = null, string? balancedModelPath = null, string? bundledModelDirectory = null)
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueJays");
    internal string UserDataDirectory { get; } = dataDirectory ?? DataDirectory;
    private readonly string settingsPath = Path.Combine(dataDirectory ?? DataDirectory, "settings.json");
    private readonly string balancedModel = balancedModelPath ?? BalancedModelPath;
    private readonly string bundledModels = bundledModelDirectory ?? Path.Combine(AppContext.BaseDirectory, "models");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record StoredSettings(AppSettings Settings, string EncryptedApiKey);
    public static string AccuracyModelPath => Path.Combine(AppContext.BaseDirectory, "models", "ggml-small-q5_1.bin");
    public static string BalancedModelPath => Path.Combine(AppContext.BaseDirectory, "models", "ggml-base-q5_1.bin");
    private AppSettings Defaults()
    {
        // Only choose an installed model on first launch. Saved engine choices
        // are retained even when that user's model needs to be downloaded again.
        var senseVoice = Path.Combine(bundledModels, SpeechModels.SenseVoice.Folder);
        if (SpeechModels.MissingFiles(SpeechEngine.SenseVoice, senseVoice).Length == 0)
            return new() { SpeechEngine = SpeechEngine.SenseVoice, SenseVoiceModelPath = senseVoice,
                SpeechDevice = SpeechDevice.Cpu, SpeechProfileVersion = 1 };
        var bundledModel = Path.Combine(bundledModels, "ggml-tiny.bin");
        return new() { ModelPath = File.Exists(balancedModel) ? balancedModel : File.Exists(bundledModel) ? bundledModel : "", SpeechProfileVersion = 1 };
    }

    public (AppSettings Settings, string ApiKey, string? Warning) Load()
    {
        if (!File.Exists(settingsPath)) return (Defaults(), "", null);
        try
        {
            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(settingsPath)) ?? throw new JsonException();
            if (stored.Settings is null || stored.Settings.Sources is null || stored.EncryptedApiKey is null ||
                stored.Settings.Sources.Any(source => source is null)) throw new JsonException("Incomplete settings file.");
            var key = stored.EncryptedApiKey.Length == 0 ? "" : Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(stored.EncryptedApiKey), null, DataProtectionScope.CurrentUser));
            var settings = stored.Settings;
            settings = settings with
            {
                SpeechEngine = Enum.IsDefined(settings.SpeechEngine) ? settings.SpeechEngine : SpeechEngine.Whisper,
                SpeechDevice = Enum.IsDefined(settings.SpeechDevice) ? settings.SpeechDevice : SpeechDevice.Cpu,
                SpeechCpuThreads = Math.Clamp(settings.SpeechCpuThreads, 1, 12)
            };
            string? warning = null;
            if (settings.SpeechProfileVersion == 0)
            {
                if (string.Equals(Path.GetFileName(settings.ModelPath), "ggml-tiny.bin", StringComparison.OrdinalIgnoreCase) && File.Exists(balancedModel))
                {
                    settings = settings with { ModelPath = balancedModel };
                    warning = "Upgraded the legacy Tiny model to bundled quantized Base for better recognition. Small is available as a slower accuracy option in Speech recognition settings.";
                }
                settings = settings with { SpeechProfileVersion = 1, MaxPhraseSeconds = settings.MaxPhraseSeconds == 12 ? 8 : settings.MaxPhraseSeconds };
            }
            return (settings, key, warning);
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or FormatException or UnauthorizedAccessException)
        {
            return (Defaults(), "", "Saved settings could not be loaded. Re-enter the model, sources and API key in Settings.");
        }
    }

    public void Save(AppSettings settings, string apiKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        // The API key is protected for this Windows user; it is never serialized in plain text.
        var encrypted = apiKey.Length == 0 ? "" : Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), null, DataProtectionScope.CurrentUser));
        var temporary = settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new StoredSettings(settings, encrypted), JsonOptions));
        File.Move(temporary, settingsPath, overwrite: true);
    }
}
