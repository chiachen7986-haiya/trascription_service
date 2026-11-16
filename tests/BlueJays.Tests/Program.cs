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
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using BlueJays.App.Localization;

if (args.FirstOrDefault() == "--benchmark") return await SpeechBenchmark.RunAsync(args);
if (args.FirstOrDefault() == "--gpu-benchmark") return await GpuSpeechBenchmark.RunAsync(args);
if (args.FirstOrDefault() == "--routed-benchmark") return await GpuSpeechBenchmark.RunRoutedAsync(args);
if (args.FirstOrDefault() == "--language-probe") return await GpuSpeechBenchmark.ProbeLanguageAsync(args);
if (args.FirstOrDefault() == "--streaming-benchmark") return await StreamingSpeechBenchmark.RunAsync(args);
if (args.FirstOrDefault() == "--onnx-smoke") return await OnnxSpeechSmoke.RunAsync(args);
if (args.FirstOrDefault() == "--onnx-cancel") return await OnnxSpeechSmoke.CancelAsync(args);
if (args.FirstOrDefault() == "--denoise-smoke") return HistoryAndDenoisingTests.Denoise(args[1]);
if (args.FirstOrDefault() == "--live-translation") return await LiveTranslationSmoke.RunAsync();
if (args.FirstOrDefault() == "--live-token-smoke") return await LiveTranslationSmoke.TokenEfficiencyAsync();
if (args.FirstOrDefault() == "--audio-diagnostics") return AudioDiagnostics.Run();
if (args.FirstOrDefault() == "--audio-startup-smoke") return await AudioDiagnostics.StartSessionAsync();

var captions = new TranslationItem[] { new("one", "Mic", "Can we meet again later?", 1, true), new("two", "Meeting", "Yes, we have time.", 1, true) };
var options = new ConversationOptions { TargetLanguages = ["Japanese", "English"], Topic = "Scheduling", Glossary = "WHP is a project name" };
var tests = new List<(string Name, Func<Task> Run)>
{
    ("Room display holds a coherent completed card until all next translations finish", () => Sync(() =>
    {
        var vm = new MainViewModel(); var conversation = new ConversationViewModel(options); vm.Conversations.Add(conversation); vm.SelectedConversation = conversation;
        var time = DateTimeOffset.Now;
        vm.Add(new("old", "Room", time, "Previous statement.", "room", 1, true));
        vm.ApplyTranslation("old", 1, "Japanese", "前の発言。", "Translated"); vm.ApplyTranslation("old", 1, "English", "Previous statement.", "Translated");
        vm.Add(new("new", "Room", time.AddSeconds(3), "Next…", "room", 1, false));
        Check(vm.RoomOriginal == "Previous statement." && vm.DisplayTranslations.First().Text == "前の発言。", "New draft blanked the completed display.");
        vm.Add(new("new", "Room", time.AddSeconds(3), "Next statement.", "room", 2, true));
        vm.ApplyTranslation("new", 2, "Japanese", "次の発言。", "Translated");
        Check(vm.RoomOriginal == "Previous statement.", "Mixed old and new language results.");
        vm.ApplyTranslation("new", 1, "English", "Stale", "Translated");
        Check(vm.RoomOriginal == "Previous statement.", "Stale result replaced the room card.");
        vm.ApplyTranslation("new", 2, "English", "Next statement.", "Translated");
        Check(vm.RoomOriginal == "Next statement." && vm.DisplayTranslations.Last().Text == "Next statement.", "Finished card did not advance.");
        vm.Add(new("third", "Room", time.AddSeconds(8), "Unfinished next thought", "room", 1, false));
        vm.ApplyTranslation("third", 1, "Japanese", "", "Translation failed");
        Check(vm.RoomOriginal == "Next statement.", "Failed API call cleared the room display.");
        vm.SelectedConversation = new(new() { TargetLanguages = [] }); Check(!vm.DisplayTranslations.Any(), "Display leaked into another conversation.");
    })),
    ("Renaming sources updates captions and context without changing their device identity", () => Sync(() =>
    {
        var source = new SourceRow(new() { Id = "mic", DeviceId = "device-1", DeviceName = "USB Audio", Name = "Old" }); source.Name = "Alice";
        Check(source.Settings.Name == "Alice" && source.Settings.DeviceId == "device-1" && source.DeviceName == "USB Audio", "Rename changed device identity.");
        var vm = new MainViewModel(); var conversation = new ConversationViewModel(options); vm.Conversations.Add(conversation); vm.SelectedConversation = conversation;
        var caption = new Transcript("a", "Old", DateTimeOffset.Now, "We can meet later.", "mic"); vm.Add(caption); conversation.Memory.Record(caption);
        vm.RenameSource("mic", "Alice");
        Check(vm.Captions[0].Source == "Alice" && conversation.Memory.Context(new HashSet<string>()).Contains("Alice"), "Rename was not used for captions/context.");
    })),
    ("UI translations cover all languages with matching format arguments", () => Sync(() =>
    {
        foreach (var key in Localizer.Keys)
        {
            var english = Localizer.For("en", key);
            foreach (var language in Localizer.SupportedLanguages)
            {
                var translated = Localizer.For(language, key);
                Check(translated.Length > 0, "Missing translation.");
                var placeholders = System.Text.RegularExpressions.Regex.Matches(english, @"\{\d+\}").Select(m => m.Value).Order();
                Check(placeholders.SequenceEqual(System.Text.RegularExpressions.Regex.Matches(translated, @"\{\d+\}").Select(m => m.Value).Order()), "Mismatched localized placeholders: " + key);
            }
        }
        Localizer.Current.SetLanguage("ja"); Check(Localizer.T("Settings") == "設定", "Japanese UI unavailable.");
        Localizer.Current.SetLanguage("zh-Hans"); Check(Localizer.T("Settings") == "设置", "Simplified Chinese UI unavailable.");
        Localizer.Current.SetLanguage("zh-Hant"); Check(Localizer.T("API configuration") == "API設定", "Traditional Chinese UI unavailable.");
        Localizer.Current.SetLanguage("en");
    })),
    ("Export summary includes tasks, owners, deadlines and proposed meetings above transcript", async () =>
    {
        using var http = new HttpClient(new FakeHandler(async (request, token) =>
        {
            using var root = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var prompt = root.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Check(prompt.Contains("Do not invent") && prompt.Contains("proposed") && prompt.Contains("confirmed"), "Summary lacks grounding/scheduling instructions.");
            return SseJson("{\"overview\":\"Project planning.\",\"decisions\":[],\"action_items\":[{\"task\":\"Send report\",\"owner\":\"Alice\",\"due\":\"Friday\"}],\"next_meetings\":[{\"details\":\"Could meet later\",\"status\":\"proposed\"}]}");
        }));
        var path = Path.Combine(Path.GetTempPath(), "BlueJays-export-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var snapshot = new ExportSnapshot("Planning", "English / Japanese", "en", [new(new(DateTimeOffset.Now, "Alice", "I will send the report Friday. We could meet later.", true), [])]);
            var result = await new TranscriptExporter(http).ExportAsync(path, snapshot, new(), "test-key", false, null, default);
            var text = await File.ReadAllTextAsync(path);
            Check(result.SummaryError is null && text.IndexOf("Meeting summary", StringComparison.Ordinal) < text.IndexOf("\nTranscript", StringComparison.Ordinal), "Summary missing or not at top.");
            Check(text.Contains("Owner: Alice") && text.Contains("Due: Friday") && text.Contains("[Proposed] Could meet later") && !text.Contains("[Scheduled]"), "Task/date/proposal changed.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }),
    ("Summary failure and cancellation still export the raw transcript", async () =>
    {
        foreach (var mode in new[] { "unconfigured", "failure", "cancel" })
        {
            using var http = new HttpClient(new FakeHandler(async (_, token) =>
            {
                if (mode == "unconfigured") throw new Exception("Should not contact an unconfigured API.");
                if (mode == "cancel") await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }));
            var path = Path.Combine(Path.GetTempPath(), "BlueJays-fallback-" + Guid.NewGuid().ToString("N") + ".txt");
            using var stop = new CancellationTokenSource(30);
            try
            {
                var snapshot = new ExportSnapshot("Meeting", "English", "en", [new(new(DateTimeOffset.Now, "Room", "Keep this original text.", true), [])]);
                var result = await new TranscriptExporter(http).ExportAsync(path, snapshot, new(), mode == "unconfigured" ? "" : "test-key", false, null, stop.Token);
                Check(result.SummaryError is not null && (await File.ReadAllTextAsync(path)).Contains("Keep this original text."), "Summary error lost the raw transcript: " + mode);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }),
    ("Long transcript summaries include every section and reduce their results", async () =>
    {
        var calls = 0;
        using var http = new HttpClient(new FakeHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(SseJson("{\"overview\":\"No commitments made.\",\"decisions\":[],\"action_items\":[],\"next_meetings\":[]}"));
        }));
        var summary = await new MeetingSummaryService(http).GenerateAsync([
            new(DateTimeOffset.Now, "A", new string('x', 15000), true), new(DateTimeOffset.Now, "B", new string('y', 15000), true)], "English", new(), "key", null, default);
        Check(calls == 3 && summary.ActionItems.Length == 0 && summary.NextMeetings.Length == 0, "Section summary/reduction lost content or invented a schedule.");
    }),
    ("Silence and clicks do not create captions", () => Sync(() =>
    {
        var buffer = new StreamingSpeechBuffer(); var windows = new List<SpeechWindow>(); buffer.Updated += windows.Add;
        buffer.Push(new float[320000]); buffer.Push(Tone(320)); buffer.Push(new float[32000]); buffer.Flush();
        Check(windows.Count == 0, "Noise created a caption.");
    })),
    ("Drafts revise the same overlapping window; normal pauses stay together", () => Sync(() =>
    {
        var buffer = new StreamingSpeechBuffer(); var windows = new List<SpeechWindow>(); buffer.Updated += windows.Add;
        buffer.Push(Tone(32000)); buffer.Push(new float[6400]); // 400 ms pause must not end the sentence.
        buffer.Push(Tone(32000));
        Check(windows.Count >= 2 && windows.All(w => !w.IsFinal) && windows.Select(w => w.Id).Distinct().Count() == 1, "Sentence fragmented.");
        Check(windows[1].Samples.Length > windows[0].Samples.Length && windows[1].Revision > windows[0].Revision, "No overlapping revision.");
        buffer.Push(new float[19200]);
        Check(windows[^1].IsFinal && windows[^1].Id == windows[0].Id, "Pause did not finalize the same caption.");
    })),
    ("Long speech retains boundary overlap and Stop flushes its tail", () => Sync(() =>
    {
        var buffer = new StreamingSpeechBuffer(maxSeconds: 8); var windows = new List<SpeechWindow>(); buffer.Updated += windows.Add;
        foreach (var sample in Tone(16000 * 10)) buffer.Push(new[] { sample });
        buffer.Flush();
        Check(windows.Count(w => w.IsFinal) == 2, "Hard boundary or tail missing.");
        Check(windows[^1].HasOverlap && windows.All(w => w.Samples.Length <= 128000), "Audio overlap or maximum length incorrect.");
    })),
    ("Coalescing preserves other utterances and drains after completion", async () =>
    {
        var queue = new CoalescingQueue<string>(2);
        Check(queue.Put("a", "draft") && queue.Put("b", "other") && queue.Put("a", "final"), "Could not coalesce.");
        Check(!queue.Put("c", "overflow"), "Capacity limit ignored."); queue.Complete();
        Check((await queue.TakeAsync(4, default)).SequenceEqual(new[] { "final", "other" }), "Lost or reordered work.");
        Check((await queue.TakeAsync(1, default)).Count == 0, "Completed queue did not finish.");
    }),
    ("Waiting queues honor cancellation", async () =>
    {
        var queue = new CoalescingQueue<string>(); using var stop = new CancellationTokenSource(30);
        try { await queue.TakeAsync(1, stop.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }),
    ("Stable text and overlap handling work for Latin and CJK", () => Sync(() =>
    {
        Check(TextStability.CommonPrefix("We will arrange", "We will arrive tomorrow") == "We will", "Confirmed half a word.");
        Check(TextStability.TranslationPrefix("We have time, so we can") == "We have time,", "Clause boundary missing.");
        Check(TextStability.RemoveOverlap("We can meet again later.", "meet again later, if needed.") == "if needed.", "Latin overlap repeated.");
        Check(TextStability.RemoveOverlap("この後再び会議ができます。", "会議ができます。その後、確認します。") == "その後、確認します。", "CJK overlap repeated.");
    })),
    ("Conversation memory shares source turns, protects revisions and stays bounded", () => Sync(() =>
    {
        var memory = new ConversationMemory();
        memory.Record(new("a", "Mic", DateTimeOffset.Now.AddSeconds(-3), "What about the budget?", "mic"));
        memory.SetTranslation("a", 1, "Japanese", "予算はどうですか？");
        memory.Record(new("b", "Meeting", DateTimeOffset.Now, "It is approved.", "meeting", 2));
        var history = memory.Context(new HashSet<string> { "b" });
        Check(history.Contains("budget") && !history.Contains("approved"), "History/current text were mixed.");
        Check(memory.SourcePrompt("mic").Contains("budget") && !memory.SourcePrompt("mic").Contains("approved"), "ASR prompts leaked between sources.");
        Check(!memory.IsCurrent("b", 1) && memory.IsCurrent("b", 2), "Stale revision accepted.");
        for (var i = 0; i < 50; i++) memory.Record(new(i.ToString(), "Room", DateTimeOffset.Now.AddSeconds(i), new string('x', 500)));
        Check(Encoding.UTF8.GetByteCount(memory.Context(new HashSet<string>())) < 8500, "Context grew without bounds.");
        Check(new ConversationMemory().Context(new HashSet<string>()) == "[]", "Memory leaked to a new conversation.");
    })),
    ("Contextual multilingual batch uses one SSE request and exact mappings", async () =>
    {
        var requests = 0;
        using var http = new HttpClient(new FakeHandler(async (request, token) =>
        {
            requests++; Check(request.Headers.Authorization?.Parameter == "test-key", "Missing authentication.");
            using var root = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Check(!root.RootElement.TryGetProperty("thinking", out _), "Unexpected provider extension.");
            using var input = JsonDocument.Parse(root.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            Check(input.RootElement.GetProperty("current_captions").GetArrayLength() == 2 && input.RootElement.GetProperty("target_languages").GetArrayLength() == 2, "Batch/languages missing.");
            Check(input.RootElement.GetProperty("conversation_history")[0].GetProperty("source").GetString() == "Other speaker", "Shared conversation history missing.");
            Check(input.RootElement.GetProperty("glossary").GetString()!.Contains("WHP"), "Glossary missing.");
            var result = JsonSerializer.Serialize(new { corrections = captions.Select(c => new { id = c.Id, text = c.Text }), translations = captions.SelectMany(c => options.TargetLanguages.Select(l => new { id = c.Id, language = l, text = l == "Japanese" ? "後で会議ができます。" : "We can meet later." })) });
            return SseJson(result);
        }));
        var translated = await new TranslationClient(http).TranslateBatchAsync(captions, "[{\"source\":\"Other speaker\",\"text\":\"We have time.\"}]", options, new() { SendThinkingDisabled = false }, "test-key");
        Check(requests == 1 && translated.Count == 4 && translated.Any(t => t.Text.Contains("会議")), "Batch/Unicode result incorrect.");
    }),
    ("Missing and duplicate translations are rejected", () => Sync(() =>
    {
        foreach (var json in new[] { "{\"translations\":[]}", "{\"translations\":[{\"id\":\"unexpected\",\"language\":\"Japanese\",\"text\":\"oops\"}]}",
            "{\"translations\":[{\"id\":\"one\",\"language\":\"Japanese\",\"text\":\"a\"},{\"id\":\"one\",\"language\":\"Japanese\",\"text\":\"b\"}]}" })
        {
            try { TranslationClient.ParseResults(json, captions, options.TargetLanguages); throw new Exception("Invalid mapping accepted."); }
            catch (InvalidOperationException) { }
        }
    })),
    ("API endpoints, HTTP errors and cancellation remain actionable", async () =>
    {
        Check(TranslationClient.GetEndpoint("https://example.com/v1").AbsoluteUri.EndsWith("/v1/chat/completions"), "Wrong endpoint.");
        _ = TranslationClient.GetEndpoint("http://localhost:1234/v1");
        foreach (var value in new[] { "http://example.com", "file:///secret", "https://user:pass@example.com", "https://example.com?key=secret", "https://example.com/v1/chat/completions" })
        { try { TranslationClient.GetEndpoint(value); throw new Exception("Invalid endpoint accepted."); } catch (ArgumentException) { } }
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        try { await new TranslationClient(http).TranslateBatchAsync(captions, "[]", options, new(), ""); throw new Exception("HTTP error ignored."); }
        catch (HttpRequestException ex) { Check(ex.Message.Contains("401"), "Status missing."); }
        using var slow = new HttpClient(new FakeHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return SseJson(""); }));
        using var stop = new CancellationTokenSource(30);
        try { await new TranslationClient(slow).TranslateBatchAsync(captions, "[]", options, new(), "", stop.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }),
    ("UI revisions do not add rows or accept stale translations; conversations stay separate", () => Sync(() =>
    {
        var vm = new MainViewModel(); var first = new ConversationViewModel(options); vm.Conversations.Add(first); vm.SelectedConversation = first;
        vm.Add(new("a", "Mic", DateTimeOffset.Now, "We have", "mic", 1, false));
        vm.Add(new("a", "Mic", DateTimeOffset.Now, "We have time.", "mic", 2, true));
        vm.Captions[0].SetTranslation(1, "Japanese", "wrong", "Draft");
        Check(vm.Captions.Count == 1 && vm.Captions[0].Translations[0].Text == "", "Duplicate row or stale translation.");
        vm.Captions[0].SetTranslation(2, "Japanese", "時間があります。", "Translated");
        Check(vm.Captions[0].Translations[0].Text.Length > 0 && vm.DisplayTranslations.Count() == 2, "Valid output missing.");
        vm.SelectedConversation = new(new() { TargetLanguages = ["French"] });
        Check(vm.Captions.Count == 0 && vm.DisplayTranslations.Single().Language == "French", "Conversation UI leaked captions/languages.");
        vm.SelectedConversation = first; Check(vm.Captions.Count == 1, "Switching lost history.");
    })),
    ("Settings keep API keys encrypted and recover from malformed data", () => Sync(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "BlueJays-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(folder); store.Save(new() { UiLanguage = "ja", UseGpuAcceleration = false, ParallelLanguageDecoding = true,
                Sources = [new() { Name = "Alice", DeviceName = "USB microphone", DeviceId = "device-1" }] }, "private-test-secret");
            Check(!File.ReadAllText(Path.Combine(folder, "settings.json")).Contains("private-test-secret"), "Plaintext API key.");
            Check(store.Load().ApiKey == "private-test-secret", "DPAPI round-trip failed.");
            Check(store.Load().Settings.UiLanguage == "ja" && store.Load().Settings.Sources[0].Name == "Alice" && store.Load().Settings.Sources[0].DeviceId == "device-1", "UI language or renamed source was not persisted.");
            Check(!store.Load().Settings.UseGpuAcceleration && store.Load().Settings.ParallelLanguageDecoding, "GPU preferences did not round-trip.");
            File.WriteAllText(Path.Combine(folder, "settings.json"), "{\"Settings\":{\"ModelPath\":\"company-model.bin\",\"Sources\":[]},\"EncryptedApiKey\":\"\"}");
            Check(store.Load().Settings.UseGpuAcceleration && !store.Load().Settings.ParallelLanguageDecoding && store.Load().Settings.ModelPath == "company-model.bin", "Legacy GPU defaults replaced the selected model.");
            File.WriteAllText(Path.Combine(folder, "settings.json"), "{broken"); Check(store.Load().Warning is not null, "Corrupt data ignored.");
        }
        finally { foreach (var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
    }))
};

tests.AddRange(PipelineTests.Cases());
tests.AddRange(TranslationRecoveryTests.Cases());
tests.AddRange(AudioStartupTests.Cases());
tests.AddRange(SpeechRoutingTests.Cases());
tests.AddRange(SpeechEngineTests.Cases());
tests.AddRange(HistoryAndDenoisingTests.Cases());
tests.AddRange(ReleaseDefaultsTests.Cases());
tests.AddRange(TokenEfficiencyTests.Cases());

if (args.Length >= 3 && args[0] == "--integration")
{
    var model = Path.GetFullPath(args[1]); var wav = Path.GetFullPath(args[2]);
    tests.Add(("Real Whisper recognizes overlapping revisions and final speech", async () =>
    {
        using var transcriber = new WhisperTranscriber(model, "en"); using var reader = new WaveFileReader(wav);
        var provider = reader.ToSampleProvider(); var samples = new float[reader.SampleCount];
        var count = provider.Read(samples, 0, samples.Length); Array.Resize(ref samples, count);
        var buffer = new StreamingSpeechBuffer(); var windows = new List<SpeechWindow>(); buffer.Updated += windows.Add;
        buffer.Push(samples); buffer.Flush(); Check(windows.Any(w => !w.IsFinal) && windows[^1].IsFinal, "No live windows.");
        var draft = await transcriber.TranscribeAsync(windows[1].Samples, default);
        var text = await transcriber.TranscribeAsync(samples, default);
        Console.WriteLine("  Recognized: " + text);
        Check(draft.Length > 0 && text.Contains("country", StringComparison.OrdinalIgnoreCase), "Real recognition failed.");
    }));
    tests.Add(("WASAPI loopback finalizes after playback ends", async () =>
    {
        using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var ready = new TaskCompletionSource<CapturedWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new AudioCaptureSource(new() { Name = "Test playback", DeviceId = device.ID, Kind = AudioSourceKind.ComputerAudio }, new() { SpeechThreshold = .002 });
        source.WindowReady += window => { if (window.Window.IsFinal) ready.TrySetResult(window); };
        source.Failed += message => ready.TrySetException(new Exception(message)); source.Start();
        using var playback = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
        playback.Init(new SignalGenerator(16000, 1) { Frequency = 440, Gain = .15 }.Take(TimeSpan.FromMilliseconds(650))); playback.Play();
        Check((await ready.Task.WaitAsync(TimeSpan.FromSeconds(15))).Window.Samples.Length >= 3200, "Loopback phrase missing.");
        playback.Stop();
    }));
    tests.Add(("Complete capture → revised STT → multilingual API pipeline finishes its last caption", async () =>
    {
        using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var observed = new Dictionary<string, Transcript>(); var translated = new HashSet<string>(); var errors = new List<string>();
        var sessionOptions = options with { SpokenLanguages = ["en"] };
        var http = new HttpClient(new FakeHandler(async (request, token) =>
        {
            using var root = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            using var input = JsonDocument.Parse(root.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var items = input.RootElement.GetProperty("current_captions").EnumerateArray().ToArray();
            return SseJson(JsonSerializer.Serialize(new { corrections = items.Select(c => new { id = c.GetProperty("id").GetString(), text = c.GetProperty("text").GetString() }),
                translations = items.SelectMany(c => c.GetProperty("translate_into").EnumerateArray().Select(language => new { id = c.GetProperty("id").GetString(), language = language.GetString(), text = "Test translation" })) }));
        }));
        await using var session = new InterpretationSession(new() { ModelPath = model, SpeechThreshold = .002,
            Sources = [new() { Id = "integration", Name = "Test playback", DeviceId = device.ID, Kind = AudioSourceKind.ComputerAudio }] }, "", sessionOptions, new(), http);
        session.Recognized += caption => { lock (observed) observed[caption.Id] = caption; };
        session.TranslationChanged += (id, revision, language, text, status) => { if (status == "Translated") lock (translated) translated.Add(id + language); };
        session.Failed += errors.Add;
        await session.StartAsync();
        using var reader = new WaveFileReader(wav); using var playback = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
        playback.Init(reader.ToSampleProvider().Take(TimeSpan.FromSeconds(4.3))); playback.Play();
        await Task.Delay(4800); playback.Stop(); await session.FinishAsync();
        Check(errors.Count == 0, string.Join("; ", errors));
        Check(observed.Count > 0 && observed.Values.All(c => c.IsFinal), "Stop lost the last caption or left a draft.");
        Check(translated.Count == observed.Count * 2, "Final translations were lost or stale.");
    }));
    tests.Add(("Real neural detection rejects silence and non-speech tones", async () =>
    {
        using var transcriber = new WhisperTranscriber(model, "en", neuralDetection: true);
        Check((await transcriber.RecognizeAsync(new float[32000], default)).Count == 0, "Silence reached recognition.");
        Check((await transcriber.RecognizeAsync(Tone(32000), default)).Count == 0, "Tone was treated as speech.");
    }));
    if (args.Length > 3) tests.Add(("Real Japanese recognition and neural detection preserve Japanese speech", async () =>
    {
        using var reader = new WaveFileReader(args[3]); var samples = new float[reader.SampleCount];
        var count = reader.ToSampleProvider().Read(samples, 0, samples.Length); Array.Resize(ref samples, count);
        using var transcriber = new WhisperTranscriber(model, "auto", neuralDetection: true, allowedLanguages: ["ja", "en"]);
        var text = await transcriber.TranscribeAsync(samples, default);
        Console.WriteLine("  Japanese: " + text);
        Check(text.Contains("中学") && text.Contains("パン"), "Japanese speech was lost or decoded in the wrong language.");
    }));
}
if (args.Length >= 2 && args[0] == "--render-ui") tests.Add(("Render all conversation and display layouts without desktop input", () => UiSmoke.Run(Path.GetFullPath(args[1]))));
var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed."); return failures == 0 ? 0 : 1;
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static Task Sync(Action test) { test(); return Task.CompletedTask; }
static float[] Tone(int length) => Enumerable.Range(0, length).Select(i => (float)(.2 * Math.Sin(i * 2 * Math.PI * 440 / 16000))).ToArray();
static HttpResponseMessage SseJson(string json)
{
    var body = new StringBuilder(": keepalive\n\n");
    foreach (var chunk in json.Chunk(37)) body.Append("data: ").Append(JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = new string(chunk) } } } })).Append("\n\n");
    body.Append("data: [DONE]\n\n"); return new(HttpStatusCode.OK) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream") };
}
sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handler(request, token); }
