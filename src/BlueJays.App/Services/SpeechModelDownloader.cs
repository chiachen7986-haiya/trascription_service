using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using BlueJays.Core.Models;

namespace BlueJays.App.Services;

/// <summary>Download to a unique staging folder; only verified, complete exports become selectable.</summary>
public static class SpeechModelDownloader
{
    public static async Task<string> DownloadAsync(SpeechEngine engine, IProgress<double> progress, CancellationToken token,
        string? modelRoot = null)
    {
        var package = SpeechModels.Package(engine);
        var root = modelRoot ?? Path.Combine(SettingsStore.DataDirectory, "models");
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, package.Folder);
        if (SpeechModels.MissingFiles(engine, destination).Length == 0) return destination;
        // Never replace or recursively remove an existing user-staged model.
        if (Directory.Exists(destination)) throw new IOException("This model folder is incomplete. Choose a different folder or repair the staged copy.");
        var staging = Path.Combine(root, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var archive = Path.Combine(staging, "model.tar.bz2");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(45) };
            using var response = await client.GetAsync(package.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(archive))
            {
                var buffer = new byte[131072]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token); total += read;
                    progress.Report(Math.Min(.94, total / (double)package.DownloadBytes * .94));
                }
                if (total != package.DownloadBytes) throw new IOException("The model download was incomplete.");
            }
            await using (var input = File.OpenRead(archive))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The model checksum did not match. Download it again.");
            progress.Report(.95);
            // Windows includes libarchive's tar. An exact SHA-256 check precedes
            // extraction of the pinned official archive, including its license.
            await ExtractAsync(archive, staging, token);
            var extracted = Path.Combine(staging, package.Folder);
            var missing = SpeechModels.MissingFiles(engine, extracted);
            if (missing.Length > 0) throw new IOException("Incomplete model export: " + string.Join(", ", missing));
            var license = Path.Combine(AppContext.BaseDirectory, "licenses", engine == SpeechEngine.SenseVoice
                ? "SenseVoice-FunASR-MODEL-LICENSE.txt" : "Qwen3-ASR-Apache-2.0.txt");
            if (File.Exists(license)) File.Copy(license, Path.Combine(extracted, "MODEL-LICENSE.txt"), true);
            await File.WriteAllTextAsync(Path.Combine(extracted, "bluejays-model.json"), JsonSerializer.Serialize(new
            { source = package.DownloadUrl, sha256 = package.Sha256, downloadedUtc = DateTimeOffset.UtcNow }), token);
            token.ThrowIfCancellationRequested(); Directory.Move(extracted, destination);
            progress.Report(1); return destination;
        }
        finally
        {
            // Only our GUID staging directory is owned by the downloader.
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static async Task ExtractAsync(string archive, string folder, CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-xf", archive, "-C", folder }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Windows tar.");
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var errors = process.StandardError.ReadToEndAsync(token); var output = process.StandardOutput.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            // Release tar's file handles before the staging folder is removed.
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None); throw;
        }
        await output;
        if (process.ExitCode != 0) throw new IOException("Could not extract the speech model: " + await errors);
    }
}
