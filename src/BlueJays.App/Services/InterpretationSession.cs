using System.Net.Http;
using System.Diagnostics;
using System.Collections.Concurrent;
using BlueJays.Core.Models;
using BlueJays.Core.Conversation;
using BlueJays.Core.Translation;
using L = BlueJays.App.Localization.Localizer;

namespace BlueJays.App.Services;

/// <summary>Revision-aware local STT and batched multilingual translation, scoped to one conversation.</summary>
public sealed class InterpretationSession : IAsyncDisposable
{
    private readonly AppSettings settings;
    private readonly string apiKey;
    private readonly ConversationOptions conversation;
    private readonly ConversationMemory memory;
    private readonly CancellationTokenSource cancellation = new();
    private readonly SpeechWorkQueue audio = new();
    private readonly CoalescingQueue<TranslationItem> translations = new(500);
    private readonly List<IAudioCaptureSource> sources = [];
    private readonly HttpClient http;
    private readonly Action<ApiTokenUsage?>? recordUsage;
    private readonly Func<AudioSourceSettings, IAudioCaptureSource> createCapture;
    private IConversationSpeechRecognizer? transcriber;
    private sealed class UtteranceState
    {
        public LiveClauseAssembler Assembler { get; } = new();
        public long CachedEnd = -1, CachedStart = -1;
        public IReadOnlyList<RecognizedPart> Parts = [];
    }
    private readonly ConcurrentDictionary<string, UtteranceState> utterances = new();
    private long lastLagNotice;
    private Task speechWorker = Task.CompletedTask, translationWorker = Task.CompletedTask;
    private bool disposed, finishing;
    public event Action<Transcript>? Recognized;
    public event Action<string, int, string, string, string>? TranslationChanged;
    public event Action<string, double>? LevelChanged;
    public event Action<string>? Warning;
    public event Action<string>? Failed;
    public event Action<string, string>? SourceStateChanged;
    public event Action<double>? SpeechLagChanged;
    public int ActiveSourceCount => sources.Count;
    public string BackendName => transcriber?.BackendName ?? "";
    public string BackendDescription => BackendName;

    public InterpretationSession(AppSettings settings, string apiKey, ConversationOptions conversation, ConversationMemory memory, HttpClient? http = null,
        Func<AudioSourceSettings, IAudioCaptureSource>? createCapture = null, Action<ApiTokenUsage?>? recordUsage = null)
    {
        this.settings = settings; this.apiKey = apiKey; this.conversation = conversation; this.memory = memory;
        this.http = http ?? new() { Timeout = Timeout.InfiniteTimeSpan };
        this.recordUsage = recordUsage;
        this.createCapture = createCapture ?? (source => new AudioCaptureSource(source, settings));
    }

    public async Task StartAsync()
    {
        try
        {
            transcriber = await Task.Run(() => ConversationSpeechRecognizer.Create(settings, conversation));
            await transcriber.WarmAsync(cancellation.Token);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(L.F("Could not load the speech model: {0}", ex.Message) + " " +
                L.T("Check the model file and Visual C++ 2022 x64 runtime."), ex);
        }
        var result = await AudioSourceStartup.StartAsync(settings.Sources,
            createCapture, capture =>
        {
            capture.WindowReady += window =>
            {
                if (!audio.Put(window) && !cancellation.IsCancellationRequested)
                    Failed?.Invoke("Speech recognition cannot keep up. Stop and use a smaller model or fewer sources; no further audio can be safely queued.");
            };
            capture.LevelChanged += (id, level) => LevelChanged?.Invoke(id, level);
            capture.Failed += message =>
            {
                // Failed/retried startup candidates are not live session sources.
                if (!sources.Contains(capture)) return;
                SourceStateChanged?.Invoke(capture.SourceId, "Unavailable · reconnect or choose device"); Failed?.Invoke(message);
            };
        }, cancellation.Token);
        sources.AddRange(result.Captures);
        foreach (var issue in result.Issues)
            SourceStateChanged?.Invoke(issue.Source.Id, "Unavailable · reconnect or choose device");
        foreach (var capture in sources) SourceStateChanged?.Invoke(capture.SourceId, "Listening");
        if (sources.Count == 0)
            throw new InvalidOperationException(L.T("None of the selected audio sources could start.") + "\n" + string.Join("\n", result.Issues.Select(i => i.Message)));
        if (result.Issues.Count > 0) Warning?.Invoke(string.Join("\n", result.Issues.Select(i => i.Message)));
        if (transcriber.StartupWarning is { } startupWarning && (settings.SpeechEngine != SpeechEngine.Whisper || settings.UseGpuAcceleration))
            Warning?.Invoke(L.T(startupWarning));
        speechWorker = Task.Run(RunSpeechWorkersAsync); translationWorker = Task.Run(TranslationLoopAsync);
    }

    private async Task RunSpeechWorkersAsync()
    {
        // Task.Run starts both consumers independently. Each leases an utterance;
        // the recognizer routes it to a language lane and controls GPU admission.
        try { await Task.WhenAll(Enumerable.Range(0, transcriber!.WorkerCount).Select(_ => Task.Run(SpeechLoopAsync))); }
        finally { translations.Complete(); }
    }

    private async Task SpeechLoopAsync()
    {
        try
        {
            while (true)
            {
                var next = await audio.TakeAsync(cancellation.Token);
                if (next is null) break;
                var capture = next; var window = capture.Window;
                try
                {
                    var lagBeforeDecode = (DateTimeOffset.Now - window.StartedAt.AddSeconds(window.SpeechEndSample / 16000d)).TotalSeconds;
                    // Final windows retain all speech. Obsolete previews are optional and
                    // should not consume GPU time while completed utterances are waiting.
                    if (!window.IsFinal && (audio.HasFinalPending || lagBeforeDecode > 2)) continue;
                    var state = utterances.GetOrAdd(window.Id, _ => new());
                    var assembler = state.Assembler;
                    // Queued snapshots may predate the last acknowledgement. Apply the
                    // same safe cut before decoding them, keeping 300 ms of overlap.
                    var cut = (int)Math.Clamp(assembler.CommittedSample - 4800 - window.StartSample, 0, Math.Max(0, window.Samples.Length - 8000));
                    if (cut > 0) { window = window with { Samples = window.Samples[cut..], StartSample = window.StartSample + cut }; capture = capture with { Window = window }; }
                    var watch = Stopwatch.StartNew();
                    IReadOnlyList<RecognizedPart> parts;
                    var decoded = false;
                    // A final pause often contains speech already decoded. Reuse that
                    // result instead of doing another complete inference on silence.
                    if (window.SpeechEndSample > 0 && state.CachedEnd == window.SpeechEndSample && state.CachedStart == window.StartSample)
                        parts = state.Parts;
                    else
                    {
                        var prompt = WhisperTranscriber.BuildPrompt(conversation, memory.SourcePrompt(capture.SourceId));
                        parts = await transcriber!.RecognizeAsync(capture, cancellation.Token, prompt);
                        decoded = true;
                        state.Parts = parts; state.CachedEnd = window.SpeechEndSample; state.CachedStart = window.StartSample;
                    }
                    var update = assembler.Update(capture, parts, memory.SourcePrompt(capture.SourceId));
                    sources.FirstOrDefault(s => s.SourceId == capture.SourceId)?.Acknowledge(window.Id, update.CommittedSample, decoded ? watch.Elapsed.TotalSeconds : 0);
                    var lag = Math.Max(0, (DateTimeOffset.Now - window.StartedAt.AddSeconds(window.SpeechEndSample / 16000d)).TotalSeconds);
                    SpeechLagChanged?.Invoke(lag);
                    var now = Environment.TickCount64; var previousNotice = Interlocked.Read(ref lastLagNotice);
                    if (lag >= 15 && now - previousNotice >= 15000 && Interlocked.CompareExchange(ref lastLagNotice, now, previousNotice) == previousNotice)
                        Warning?.Invoke(L.F("Speech recognition is {0:F0} seconds behind. Use the balanced model or fewer audio sources.", lag));
                    foreach (var transcript in update.Captions)
                    {
                        memory.Record(transcript); Recognized?.Invoke(transcript);
                        // Confirmed clauses have immutable identities. A later tail
                        // cannot invalidate a response for an earlier clause.
                        if (transcript.IsFinal && conversation.TargetLanguages.Count > 0)
                            if (!translations.Put(transcript.Id, new(transcript.Id, capture.SourceName, transcript.Text, transcript.Revision, true, transcript.Language)))
                                Failed?.Invoke("Translation backlog is full. Stop and check provider responsiveness before resuming.");
                    }
                }
                finally
                {
                    if (window.IsFinal)
                    { utterances.TryRemove(window.Id, out _); await transcriber!.ForgetAsync(window.Id); }
                    audio.Release(window.Id);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { Failed?.Invoke("Speech recognition stopped: " + ex.Message); await cancellation.CancelAsync(); }
    }

    private async Task TranslationLoopAsync()
    {
        var client = new TranslationClient(http, recordUsage);
        client.Notice += message => Warning?.Invoke(message);
        try
        {
            while (true)
            {
                var batch = await translations.TakeAsync(3, cancellation.Token, item => item.Text.Length, 1800);
                if (batch.Count == 0) break;
                foreach (var item in batch) foreach (var language in conversation.TargetLanguages)
                    TranslationChanged?.Invoke(item.Id, item.Revision, language, "", "Translating…");
                var delivered = new HashSet<(string Id, string Language)>();
                try
                {
                    var context = memory.Context(batch.Select(c => c.Id).ToHashSet());
                    try { await SendAsync(settings); }
                    catch (Exception ex) when (settings.ContextualCorrectionEnabled && ex is InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException)
                    {
                        foreach (var item in batch) if (memory.ApplyCorrection(item, item.Text) is { } restored) Recognized?.Invoke(restored);
                        delivered.Clear(); // Restoring raw text invalidates correction-derived outputs.
                        Warning?.Invoke("Context correction was unavailable or rejected; using raw recognition for this clause.");
                        if (conversation.TargetLanguages.Count > 0) await SendAsync(settings with { ContextualCorrectionEnabled = false });
                    }

                    async Task SendAsync(AppSettings requestSettings)
                    {
                        await client.TranslateBatchAsync(batch, context, conversation, requestSettings, apiKey, cancellation.Token, result =>
                        {
                            var item = batch.First(c => c.Id == result.Id);
                            if (!memory.TryResolve(item, out var current)) return;
                            memory.SetTranslation(item.Id, current.Revision, result.Language, result.Text);
                            delivered.Add((item.Id, result.Language));
                            TranslationChanged?.Invoke(item.Id, current.Revision, result.Language, result.Text, "Translated");
                        }, correction =>
                        {
                            var item = batch.First(c => c.Id == correction.Id);
                            if (memory.ApplyCorrection(item, correction.Text) is { } corrected) Recognized?.Invoke(corrected);
                        });
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    foreach (var item in batch) if (memory.TryResolve(item, out var current)) foreach (var language in conversation.TargetLanguages)
                        if (!delivered.Contains((item.Id, language)))
                            TranslationChanged?.Invoke(item.Id, current.Revision, language, "", "Translation failed");
                    Warning?.Invoke(ex is OperationCanceledException ? "Translation timed out (30 seconds)." : "Translation failed: " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    public async Task FinishAsync()
    {
        if (finishing) return; finishing = true;
        foreach (var source in sources) source.Finish();
        audio.Complete();
        try { await Task.WhenAll(speechWorker, translationWorker).WaitAsync(TimeSpan.FromSeconds(35)); }
        catch (TimeoutException) { Warning?.Invoke("Finishing timed out. Some captions remain incomplete; their text is retained."); await cancellation.CancelAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        await cancellation.CancelAsync();
        foreach (var source in sources) source.Dispose();
        audio.Complete(); translations.Complete();
        await Task.WhenAll(speechWorker, translationWorker);
        if (transcriber is IAsyncDisposable asyncTranscriber) await asyncTranscriber.DisposeAsync();
        else transcriber?.Dispose();
        http.Dispose(); cancellation.Dispose();
    }
}
