using System.IO;
using System.Runtime.InteropServices;
using BlueJays.App.Services;
using BlueJays.App.ViewModels;
using BlueJays.Core.Models;

internal static class AudioStartupTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("An unplugged saved source does not prevent another selected source starting", async () =>
        {
            var requested = new List<string>(); var good = new FakeCapture("good");
            var result = await AudioSourceStartup.StartAsync([Source("bad"), Source("good")], source =>
            {
                requested.Add(source.DeviceId);
                if (source.Id == "bad") throw new AudioDeviceUnavailableException("The selected audio device is disconnected or disabled.");
                return good;
            }, _ => { }, default);
            Check(result.Captures.Single() == good && good.Starts == 1 && good.Disposals == 0, "Healthy capture was aborted.");
            Check(result.Issues.Single().Source.Id == "bad" && result.Issues[0].Message.Contains("bad"), "Failure did not identify the source.");
            Check(requested.SequenceEqual(new[] { "device-bad", "device-good" }), "Substituted an unselected endpoint.");
            good.Dispose();
        }
        );
        yield return ("Invalidated startup releases the old client and retries the same endpoint once", async () =>
        {
            var first = new FakeCapture("same", _ => throw Invalidated()); var second = new FakeCapture("same"); var calls = 0;
            var result = await AudioSourceStartup.StartAsync([Source("same")], source =>
            {
                Check(source.DeviceId == "device-same", "Retried a different endpoint.");
                if (++calls == 1) return first;
                Check(first.Disposals == 1, "Old native client was not released before retry."); return second;
            }, _ => { }, default);
            Check(calls == 2 && result.Issues.Count == 0 && result.Captures.Single() == second, "Transient reconfiguration did not recover.");
            second.Dispose();
        }
        );
        yield return ("Persistent invalidation is bounded, cleans failed captures and keeps healthy sources", async () =>
        {
            var failed = new List<FakeCapture>(); var good = new FakeCapture("good");
            var result = await AudioSourceStartup.StartAsync([Source("bad"), Source("good")], source =>
            {
                if (source.Id == "good") return good;
                var capture = new FakeCapture("bad", _ => throw Invalidated()); failed.Add(capture); return capture;
            }, _ => { }, default);
            Check(failed.Count == 2 && failed.All(c => c.Disposals == 1), "Retry loop leaked or exceeded its bound.");
            Check(result.Captures.Count == 1 && result.Issues.Single().Error.HResult == unchecked((int)0x88890004), "Other source was lost or failure code changed.");
            good.Dispose();
        }
        );
        yield return ("Permission failures are identified without retrying or blaming the speech engine", async () =>
        {
            var calls = 0;
            var result = await AudioSourceStartup.StartAsync([Source("mic")], _ =>
            { calls++; throw new COMException("Denied", unchecked((int)0x80070005)); }, _ => { }, default);
            Check(calls == 1 && result.Captures.Count == 0 && result.Issues[0].Message.Contains("privacy") && !result.Issues[0].Message.Contains("Visual C++"), "Incorrect audio error diagnosis.");
        }
        );
        yield return ("Canceling partial startup disposes both started and pending sources", async () =>
        {
            using var stop = new CancellationTokenSource(); var first = new FakeCapture("first");
            var second = new FakeCapture("second", token => { stop.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
            try
            {
                await AudioSourceStartup.StartAsync([Source("first"), Source("second")], s => s.Id == "first" ? first : second, _ => { }, stop.Token);
                throw new Exception("Cancellation ignored.");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            { Check(first.Disposals == 1 && second.Disposals == 1, "Partial startup leaked native clients."); }
        }
        );
        yield return ("Fatal startup exceptions roll back earlier captures and disabled sources are never opened", async () =>
        {
            var first = new FakeCapture("first"); var seen = new List<string>();
            try
            {
                await AudioSourceStartup.StartAsync([Source("disabled") with { Enabled = false }, Source("first"), Source("fatal")], source =>
                { seen.Add(source.Id); if (source.Id == "fatal") throw new FileNotFoundException("Dependency missing"); return first; }, _ => { }, default);
                throw new Exception("Fatal startup failure ignored.");
            }
            catch (FileNotFoundException)
            { Check(first.Disposals == 1 && seen.SequenceEqual(new[] { "first", "fatal" }), "Rollback or disabled-source selection failed."); }
        }
        );
        yield return ("A source invalidated while another endpoint opens is not reported as listening", async () =>
        {
            var invalid = false;
            var first = new FakeCapture("playback", ensure: () => { if (invalid) throw Invalidated(); });
            var second = new FakeCapture("mic", _ => { invalid = true; return Task.CompletedTask; });
            var result = await AudioSourceStartup.StartAsync([Source("playback"), Source("mic")], s => s.Id == "playback" ? first : second, _ => { }, default);
            Check(result.Captures.Single() == second && result.Issues.Single().Source.Id == "playback" && first.Disposals == 1,
                "A dead endpoint survived final startup validation.");
            second.Dispose();
        });
        yield return ("Changing a source device preserves its identity, custom label and source kind", () =>
        {
            var row = new SourceRow(Source("same") with { Name = "Alice", DeviceName = "Old headphones", Kind = AudioSourceKind.ComputerAudio });
            row.SetStatus("Unavailable · reconnect or choose device"); row.ChangeDevice("new-device", "New headphones");
            Check(row.Id == "same" && row.Name == "Alice" && row.Settings.DeviceId == "new-device" && row.DeviceName == "New headphones" && row.State == "Ready" && row.Settings.Kind == AudioSourceKind.ComputerAudio,
                "Device change replaced source identity or labels.");
            return Task.CompletedTask;
        }
        );
    }

    private static AudioSourceSettings Source(string id) => new() { Id = id, DeviceId = "device-" + id, Name = id };
    private static COMException Invalidated() => new("Invalid device", unchecked((int)0x88890004));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class FakeCapture(string id, Func<CancellationToken, Task>? start = null, Action? ensure = null) : IAudioCaptureSource
    {
        public string SourceId => id;
        public int Starts { get; private set; }
        public int Disposals { get; private set; }
        public event Action<CapturedWindow>? WindowReady { add { } remove { } }
        public event Action<string, double>? LevelChanged { add { } remove { } }
        public event Action<string>? Failed { add { } remove { } }
        public Task StartAsync(CancellationToken token) { Starts++; return start?.Invoke(token) ?? Task.CompletedTask; }
        public void EnsureRunning() => ensure?.Invoke();
        public void Dispose() => Disposals++;
        public void Finish() => Dispose();
        public void Acknowledge(string windowId, long sample, double decodeSeconds) { }
    }
}
