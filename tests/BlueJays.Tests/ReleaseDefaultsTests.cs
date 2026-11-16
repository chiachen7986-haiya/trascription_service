using System.IO;
using BlueJays.App.Services;
using BlueJays.Core.Models;

internal static class ReleaseDefaultsTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("Fresh portable installs select complete SenseVoice weights and preserve saved engine choices", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "Transcription-release-" + Guid.NewGuid().ToString("N"));
            try
            {
                var models = Path.Combine(root, "models");
                var sense = Path.Combine(models, SpeechModels.SenseVoice.Folder);
                Directory.CreateDirectory(sense);
                var store = new SettingsStore(Path.Combine(root, "settings"), Path.Combine(models, "absent.bin"), models);
                File.WriteAllText(Path.Combine(sense, "model.int8.onnx"), "fixture");
                if (store.Load().Settings.SpeechEngine == SpeechEngine.SenseVoice)
                    throw new Exception("An incomplete bundled export was selected.");
                File.WriteAllText(Path.Combine(sense, "tokens.txt"), "fixture");
                var fresh = store.Load().Settings;
                if (fresh.SpeechEngine != SpeechEngine.SenseVoice || fresh.SpeechDevice != SpeechDevice.Cpu || !SpeechModels.IsReady(fresh))
                    throw new Exception("Fresh release needs a model download or unsuitable acceleration.");
                store.Save(fresh with { SpeechEngine = SpeechEngine.Whisper, ModelPath = "company-model.bin" }, "");
                if (store.Load().Settings.SpeechEngine != SpeechEngine.Whisper || store.Load().Settings.ModelPath != "company-model.bin")
                    throw new Exception("Existing engine selection was overwritten.");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            return Task.CompletedTask;
        });
    }
}
