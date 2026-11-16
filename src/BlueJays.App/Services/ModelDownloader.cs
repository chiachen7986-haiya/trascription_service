using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace BlueJays.App.Services;

public static class ModelDownloader
{
    public static async Task<string> DownloadAsync(string size, IProgress<double> progress, CancellationToken token)
    {
        if (size is not ("tiny" or "base" or "small" or "base-q5_1" or "small-q5_1")) throw new ArgumentException("Unknown model size.");
        var folder = Path.Combine(SettingsStore.DataDirectory, "models");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, $"ggml-{size}.bin");
        if (File.Exists(destination)) return destination;
        var temporary = destination + ".partial";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            // Official whisper.cpp conversion repository. Users can browse to a company-staged model instead.
            using var response = await client.GetAsync($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{size}.bin",
                HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var output = File.Create(temporary))
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    total += read;
                    progress.Report(response.Content.Headers.ContentLength is > 0 and var length ? (double)total / length : -1);
                }
                if (total < 1_000_000) throw new IOException("The model download was incomplete.");
            }
            var expected = size switch
            {
                "tiny" => "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21",
                "base-q5_1" => "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898",
                "small-q5_1" => "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb",
                _ => ""
            };
            if (expected.Length > 0)
            {
                await using var downloaded = File.OpenRead(temporary);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, token));
                if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("The model checksum did not match. Download it again.");
            }
            // Interrupted downloads never become selectable model files.
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
