using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BlueJays.App.Services;
using BlueJays.App.ViewModels;
using BlueJays.Core.Audio;
using BlueJays.Core.Conversation;
using BlueJays.Core.Models;
using BlueJays.Core.Translation;

internal static class PipelineTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("Japanese clauses commit during continuous speech; the tail stays editable", () => Sync(() =>
        {
            var assembler = new LiveClauseAssembler(); var time = DateTimeOffset.Now;
            CapturedWindow Window(int revision, bool final = false, long offset = 0) => new("mic", "Alice", new("utterance", revision, time, new float[64000], final, false, offset));
            var first = assembler.Update(Window(1), [new("明日の資料を確認します。", 0, 1.8), new("次の", 1.8, 3.7)]);
            Check(first.Captions.Count == 1 && !first.Captions[0].IsFinal, "First guess was frozen.");
            var second = assembler.Update(Window(2), [new("明日の資料を確認します。", 0, 1.8), new("次の会議は金曜日です。", 1.8, 3.7)]);
            Check(second.Captions.Count == 2 && second.Captions[0].IsFinal && !second.Captions[1].IsFinal, "Stable clause did not finalize during ongoing speech.");
            Check(second.Captions[0].Id == first.Captions[0].Id && second.Captions[1].Id != first.Captions[0].Id, "Clause/tail identities overlap.");
            Check(second.CommittedSample == 28800, "Did not use the safe segment timestamp.");
            var third = assembler.Update(Window(3, true, 24000), [new("次の会議は金曜日です。", .3, 2.5)]);
            Check(third.Captions.Count == 1 && third.Captions[0].Id == second.Captions[1].Id && third.Captions[0].IsFinal, "Trimmed tail duplicated or vanished.");
            Check(!third.Captions[0].Text.Contains("資料"), "Committed text repeated.");
            Check(LiveClauseAssembler.CompleteClause("予算は、").Length == 0, "Short comma fragment was frozen.");
        }));
        yield return ("Committed audio is removed from future snapshots; stale acknowledgements are ignored", () => Sync(() =>
        {
            var buffer = new StreamingSpeechBuffer(); var windows = new List<SpeechWindow>(); buffer.Updated += windows.Add;
            var tone = Enumerable.Range(0, 38400).Select(i => (float)(.2 * Math.Sin(i * 2 * Math.PI * 440 / 16000))).ToArray();
            buffer.Push(tone); var original = windows[^1];
            buffer.Acknowledge("other-window", 20000, .2); buffer.Push(tone[..19200]);
            Check(windows[^1].StartSample == 0, "Wrong utterance acknowledgement trimmed audio.");
            buffer.Acknowledge(original.Id, 28800, .2); buffer.Push(tone[..19200]);
            var trimmed = windows[^1];
            Check(trimmed.StartSample == 24000 && trimmed.Samples.Length < 76800, "Committed audio was decoded again.");
            Check(trimmed.SpeechEndSample == trimmed.StartSample + trimmed.Samples.Length, "Absolute offsets drifted after trimming.");
        }));
        yield return ("No character-based audio cuts inside a partial Whisper segment", () => Sync(() =>
        {
            var assembler = new LiveClauseAssembler(); var window = new CapturedWindow("mic", "Room", new("a", 1, DateTimeOffset.Now, new float[64000], false, false));
            assembler.Update(window, [new("We approved the budget. Next", 0, 3.8)]);
            var update = assembler.Update(window, [new("We approved the budget. Next week", 0, 3.8)]);
            Check(update.Captions[0].IsFinal && update.CommittedSample == 0, "Guessed an acoustic cut from text length.");
            var spaced = new LiveClauseAssembler();
            spaced.Update(window, [new(" We approved the budget.", 0, 1.8), new(" Next", 1.8, 3.8)]);
            var safe = spaced.Update(window, [new(" We approved the budget.", 0, 1.8), new(" Next week", 1.8, 3.8)]);
            Check(safe.CommittedSample == 28800, "Whisper's leading segment space prevented audio acknowledgement.");
        }));
        yield return ("Short committed clauses do not repeat, and genuine repeated words after a cut remain", () => Sync(() =>
        {
            var assembler = new LiveClauseAssembler(); var capture = new CapturedWindow("mic", "Room", new("a", 1, DateTimeOffset.Now, new float[64000], false, false));
            assembler.Update(capture, [new("はい。はい、", 0, 3.8)]);
            var committed = assembler.Update(capture, [new("はい。はい、次に進みます", 0, 3.8)]);
            Check(committed.Captions[0].Text == "はい。" && committed.Captions[1].Text.StartsWith("はい、"), "Short clause or following repeated word was lost.");
            var last = assembler.Update(capture with { Window = capture.Window with { IsFinal = true } }, [new("はい。はい、次に進みます。", 0, 3.8)]);
            Check(last.Captions.Single().Text == "はい、次に進みます。", "Untrimmed short clause repeated in the final text.");
            var timed = new LiveClauseAssembler();
            timed.Update(capture, [new("はい。", 0, 1), new("はい、", 1, 3.8)]);
            timed.Update(capture, [new("はい。", 0, 1), new("はい、次に進みます", 1, 3.8)]);
            var afterCut = timed.Update(capture with { Window = capture.Window with { IsFinal = true, StartSample = 11200 } }, [new("はい、次に進みます。", .3, 3)]);
            Check(afterCut.Captions.Single().Text.StartsWith("はい、"), "A genuine repeated word after a timestamp cut was removed.");
        }));
        yield return ("Translations follow clause content, preserve same-text revisions, reject changed words", () => Sync(() =>
        {
            var memory = new ConversationMemory(); var caption = new Transcript("a", "Mic", DateTimeOffset.Now, "資料を確認します。", "mic", 1, true);
            var item = new TranslationItem("a", "Mic", caption.Text, 1, true); memory.Record(caption);
            memory.SetTranslation("a", 1, "English", "I will review the documents.");
            memory.Record(caption with { Revision = 2 });
            Check(memory.TryResolve(item, out var resolved) && resolved.Revision == 2, "Same content invalidated the pending request.");
            Check(memory.Context(new HashSet<string>()).Contains("review"), "Same-text revision discarded translations.");
            memory.Record(caption with { Revision = 3, Text = "資料を確認しません。" });
            Check(!memory.TryResolve(item, out _), "Changed meaning accepted a stale response.");
        }));
        yield return ("Conservative correction preserves raw Japanese and protects numbers and negation", () => Sync(() =>
        {
            const string raw = "明日の議会は3時です。"; const string corrected = "明日の会議は3時です。";
            Check(CorrectionGuard.IsConservative(raw, corrected), "Small homophone correction rejected.");
            Check(!CorrectionGuard.IsConservative(raw, "明日の会議は4時です。"), "Time was changed.");
            Check(!CorrectionGuard.IsConservative("資料は不要ではありません。", "資料は不要です。"), "Negation was changed.");
            Check(!CorrectionGuard.IsConservative("We do not approve.", "We do approve."), "English negation was changed.");
            Check(!CorrectionGuard.IsConservative("提案を承認します。", "提案を否認します。"), "Decision meaning was changed.");
            Check(!CorrectionGuard.IsConservative(raw, "来週の金曜日に全社員会議を開催することを決定しました。"), "Large invented rewrite accepted.");
            var memory = new ConversationMemory(); memory.Record(new("a", "Mic", DateTimeOffset.Now, raw, "mic"));
            var item = new TranslationItem("a", "Mic", raw, 1, true); var result = memory.ApplyCorrection(item, corrected)!;
            Check(result.RawText == raw && result.Text == corrected && memory.TryResolve(item, out _), "Raw text or content validity lost after correction.");
            Check(memory.SourcePrompt("mic") == raw, "API guess leaked into acoustic recognition prompt.");
            var row = new CaptionRow(result, ["English"]); Check(row.HasCorrection && row.RawText == raw, "Correction was invisible in UI.");
            Check(WhisperTranscriber.BuildPrompt(new() { Topic = "予定", Glossary = "経費精算、田中さん" }, "前の発言").Contains("経費精算"), "Recognition glossary missing.");
        }));
        yield return ("Streaming correction and first translation arrive before the API response finishes", StreamingResultsAsync);
        yield return ("Unsafe API correction is rejected before its translation can be displayed", async () =>
        {
            var called = false;
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response("{\"corrections\":[{\"id\":\"a\",\"text\":\"会議は4時です。\"}],\"translations\":[{\"id\":\"a\",\"language\":\"English\",\"text\":\"4pm\"}]}"))));
            try
            {
                await new TranslationClient(http).TranslateBatchAsync([new("a", "Mic", "会議は3時です。", 1, true)], "[]", new() { TargetLanguages = ["English"] }, new(), "", resultReady: _ => called = true);
                throw new Exception("Unsafe correction accepted.");
            }
            catch (InvalidOperationException) { Check(!called, "Unsafe translation escaped before correction validation."); }
        });
        yield return ("Disabling correction preserves the plain translation API contract", async () =>
        {
            var count = 0;
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                count++;
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                using var input = JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                Check(input.RootElement.GetProperty("correction_ids").GetArrayLength() == 0, "Correction was requested while disabled.");
                return Response("{\"translations\":[{\"id\":\"a\",\"language\":\"English\",\"text\":\"Expense claims.\"}]}");
            }));
            var results = await new TranslationClient(http).TranslateBatchAsync([new("a", "Mic", "経費精算。", 1, true)], "[]", new() { TargetLanguages = ["English"] }, new() { ContextualCorrectionEnabled = false }, "",
                correctionReady: _ => throw new Exception("Disabled correction was delivered."));
            Check(count == 1 && results.Single().Text == "Expense claims.", "Plain provider response rejected.");
        });
        yield return ("Legacy Tiny settings upgrade once; explicit later model choices remain", () => Sync(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "BlueJays-migration-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(folder);
                var model = Path.Combine(folder, "model-path-test.bin"); File.WriteAllText(model, "Only settings-path selection is tested here.");
                var store = new SettingsStore(folder, model); store.Save(new() { ModelPath = "ggml-tiny.bin" }, "");
                var loaded = store.Load();
                Check(loaded.Settings.ModelPath == model && loaded.Warning is not null, "Legacy Tiny was not upgraded visibly.");
                store.Save(loaded.Settings with { ModelPath = "ggml-tiny.bin" }, "");
                Check(store.Load().Settings.ModelPath == "ggml-tiny.bin", "An explicit later model choice was overwritten.");
            }
            finally { foreach (var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
        }));
        yield return ("Audio preprocessing rejects DC and invalid samples and bounds quiet-speech gain", () => Sync(() =>
        {
            var input = Enumerable.Range(0, 16000).Select(i => (float)(.01 + .003 * Math.Sin(i * Math.PI / 10))).ToArray();
            input[0] = float.NaN; var output = SpeechAudio.Prepare(input);
            Check(output.All(float.IsFinite) && output.Max(v => Math.Abs(v)) <= .04, "Gain exploded or invalid samples propagated.");
            Check(Math.Abs(output.Average()) < .0001, "DC offset retained.");
            var gate = new AdaptiveSpeechGate(.012);
            Check(!gate.IsSpeech(Enumerable.Repeat(.2f, 320).ToArray()), "DC was classified as speech.");
        }));
        yield return ("Exports retain both context-adjusted Japanese and the original STT", async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "BlueJays-raw-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                using var http = new HttpClient();
                var snapshot = new ExportSnapshot("Test", "English", "en", [new(new(DateTimeOffset.Now, "Mic", "明日の会議", true), [], "明日の議会")]);
                await new TranscriptExporter(http).ExportAsync(path, snapshot, new() { ApiBaseUrl = "" }, "", false, null, default);
                var text = await File.ReadAllTextAsync(path);
                Check(text.Contains("明日の会議") && text.Contains("明日の議会") && text.Contains("Context adjusted"), "Export hid the original recognition.");
            }
            finally { File.Delete(path); }
        });
    }

    private static async Task StreamingResultsAsync()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var corrections = new List<TranscriptCorrection>(); var results = new List<TranslationResult>();
        const string prefix = "{\"corrections\":[{\"id\":\"a\",\"text\":\"明日の会議です。\"}],\"translations\":[{\"id\":\"a\",\"language\":\"English\",\"text\":\"Tomorrow's meeting.\"}";
        const string tail = ",{\"id\":\"a\",\"language\":\"Japanese\",\"text\":\"明日の会議です。\"}]}";
        using var stream = new GatedStream(Encoding.UTF8.GetBytes(Sse(prefix)), Encoding.UTF8.GetBytes(Sse(tail) + "data: [DONE]\n\n"), finish.Task);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        var task = new TranslationClient(http).TranslateBatchAsync([new("a", "Mic", "明日の議会です。", 1, true)], "[]",
            new() { TargetLanguages = ["English", "Japanese"] }, new(), "", resultReady: result => { results.Add(result); first.TrySetResult(); }, correctionReady: corrections.Add);
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!task.IsCompleted && corrections.Single().Text == "明日の会議です。" && results.Count == 1, "Waited for the complete response.");
        }
        finally { finish.TrySetResult(); }
        Check((await task).Count == 2, "Final mapping incomplete.");
    }

    private static string Sse(string text) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } }) + "\n\n";
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(Sse(json) + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Task Sync(Action test) { test(); return Task.CompletedTask; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handle(request, token); }
    private sealed class GatedStream(byte[] prefix, byte[] tail, Task release) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => prefix.Length + tail.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (position >= prefix.Length) await release.WaitAsync(token);
            var bytes = position < prefix.Length ? prefix : tail;
            var start = position < prefix.Length ? position : position - prefix.Length;
            var count = Math.Min(buffer.Length, bytes.Length - start);
            bytes.AsMemory(start, count).CopyTo(buffer); position += count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
