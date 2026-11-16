using BlueJays.App.Services;
using BlueJays.Core.Conversation;
using BlueJays.Core.Models;

internal static class SpeechRoutingTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("Speech scheduling runs independent sources while preserving each source's order", async () =>
        {
            var queue = new SpeechWorkQueue();
            queue.Put(Capture("ja", "mic")); queue.Put(Capture("en", "mic")); queue.Put(Capture("other", "call"));
            var first = await queue.TakeAsync(default); var second = await queue.TakeAsync(default);
            Check(first!.Window.Id == "ja" && second!.Window.Id == "other", "A source overtook its predecessor or blocked another source.");
            var waiting = queue.TakeAsync(default); Check(!waiting.IsCompleted, "The same source was decoded concurrently.");
            queue.Release("ja"); Check((await waiting.WaitAsync(TimeSpan.FromSeconds(2)))!.Window.Id == "en", "Queued source did not resume.");
            queue.Release("en"); queue.Release("other"); queue.Complete();
            Check(await queue.TakeAsync(default) is null, "Completed queue did not drain.");
        }
        );
        yield return ("Speech preview coalescing retains final audio and wakes all consumers on completion", async () =>
        {
            var queue = new SpeechWorkQueue(1); var original = Capture("same", "mic");
            queue.Put(original); queue.Put(original with { Window = original.Window with { Revision = 2, IsFinal = true } });
            queue.Put(original with { Window = original.Window with { Revision = 3 } });
            var overflow = Capture("overflow", "other");
            Check(!queue.Put(overflow with { Window = overflow.Window with { IsFinal = true } }), "Final queue capacity was not bounded.");
            var last = await queue.TakeAsync(default); Check(last!.Window.IsFinal && last.Window.Revision == 2, "Final audio was replaced with a draft.");
            queue.Release("same"); var a = queue.TakeAsync(default); var b = queue.TakeAsync(default); queue.Complete();
            Check((await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(2))).All(c => c is null), "A consumer was stranded on shutdown.");
        }
        );
        yield return ("Canceling a waiting speech consumer leaves retained audio available", async () =>
        {
            var queue = new SpeechWorkQueue(); queue.Put(Capture("a", "mic")); await queue.TakeAsync(default);
            queue.Put(Capture("b", "mic")); using var cancel = new CancellationTokenSource(); var waiting = queue.TakeAsync(cancel.Token);
            cancel.Cancel(); await ExpectCanceled(waiting); queue.Release("a");
            Check((await queue.TakeAsync(default))!.Window.Id == "b", "Cancellation lost final work.");
        }
        );
        yield return ("Language detection is cached across previews and revisited during long speech", async () =>
        {
            var cache = new LanguageRouteCache(["ja", "en"]); var calls = 0;
            Task<LanguageDetection> Detect(CancellationToken _) => Task.FromResult(new LanguageDetection(++calls == 1 ? "ja" : "en", .9f));
            Check(await cache.ResolveAsync("turn", "mic", 32000, Detect, default) == "ja", "Initial route incorrect.");
            Check(await cache.ResolveAsync("turn", "mic", 64000, Detect, default) == "ja" && calls == 1, "Every preview redetected language.");
            Check(await cache.ResolveAsync("turn", "mic", 144000, Detect, default) == "en" && calls == 2, "Long-turn language change was never reconsidered.");
            await cache.ForgetAsync("turn"); await cache.ResolveAsync("turn", "mic", 16000, Detect, default);
            Check(calls == 3, "A completed utterance left stale routing state.");
        }
        );
        yield return ("Weak language evidence retains source history and unrelated languages are rejected", async () =>
        {
            var cache = new LanguageRouteCache(["ja", "en"]);
            await cache.ResolveAsync("first", "mic", 32000, _ => Task.FromResult(new LanguageDetection("ja", .9f)), default);
            var weak = await cache.ResolveAsync("second", "mic", 16000, _ => Task.FromResult(new LanguageDetection("en", .2f)), default);
            Check(weak == "ja", "Weak short speech flipped a known language.");
            var unrelated = await cache.ResolveAsync("third", "mic", 32000, _ => Task.FromResult(new LanguageDetection("fr", .9f)), default);
            Check(unrelated == "ja", "Selected a language not configured for the conversation.");
            var single = new LanguageRouteCache(["ja"]);
            Check(await single.ResolveAsync("fixed", "mic", 32000, _ => throw new Exception("Unnecessary detection"), default) == "ja", "Fixed language changed.");
        }
        );
        yield return ("Canceled detection cannot poison a later language route", async () =>
        {
            var cache = new LanguageRouteCache(["ja", "en"]); using var cancel = new CancellationTokenSource();
            await ExpectCanceled(cache.ResolveAsync("same", "mic", 32000, _ => { cancel.Cancel(); return Task.FromResult(new LanguageDetection("ja", .9f)); }, cancel.Token));
            Check(await cache.ResolveAsync("same", "mic", 32000, _ => Task.FromResult(new LanguageDetection("en", .9f)), default) == "en", "Canceled result was cached.");
        }
        );
        yield return ("Two GPU language lanes can decode independently without auto transcription", async () =>
        {
            var ja = new FakeDecoder(true); var en = new FakeDecoder(true);
            using var recognizer = new RoutedSpeechRecognizer(["ja", "en"], i => i == 0 ? ja : en, new FakeDetector(), true);
            var first = recognizer.RecognizeAsync(Capture("ja", "a"), default);
            var second = recognizer.RecognizeAsync(Capture("en", "b", -1), default);
            await Task.WhenAll(ja.Entered.Task, en.Entered.Task).WaitAsync(TimeSpan.FromSeconds(2));
            Check(ja.Language == "ja" && en.Language == "en" && recognizer.ConcurrentDecodes == 2, "Jobs were duplicated or transcribed with automatic language detection.");
            ja.Release.TrySetResult(); en.Release.TrySetResult(); await Task.WhenAll(first, second);
        }
        );
        yield return ("Shared GPU admission and CPU fallback prevent unbounded concurrent inference", async () =>
        {
            foreach (var cpuFallback in new[] { false, true })
            {
                var ja = new FakeDecoder(!cpuFallback); var en = new FakeDecoder(!cpuFallback);
                using var recognizer = new RoutedSpeechRecognizer(["ja", "en"], i => i == 0 ? ja : en, new FakeDetector(), cpuFallback);
                var first = recognizer.RecognizeAsync(Capture("ja", "a"), default); await ja.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                using var cancel = new CancellationTokenSource();
                var second = recognizer.RecognizeAsync(Capture("en", "b", -1), cancel.Token);
                await Task.Delay(30); Check(!en.Entered.Task.IsCompleted && recognizer.ConcurrentDecodes == 1, "Shared GPU or CPU admission limit was bypassed.");
                cancel.Cancel(); await ExpectCanceled(second); ja.Release.TrySetResult(); await first;
                en.Release.TrySetResult(); await recognizer.RecognizeAsync(Capture("en-retry", "b", -1), default).WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        );
        yield return ("Partial decoder startup disposes the first model and detector", () =>
        {
            var first = new FakeDecoder(true); var detector = new FakeDetector();
            try { using var unused = new RoutedSpeechRecognizer(["ja", "en"], i => i == 0 ? first : throw new InvalidOperationException("Failed GPU allocation"), detector, true); }
            catch (InvalidOperationException)
            { Check(first.Disposed && detector.Disposed, "Partial GPU startup leaked native resources."); return Task.CompletedTask; }
            throw new Exception("Startup failure was swallowed.");
        }
        );
    }

    private static CapturedWindow Capture(string id, string source, float marker = 1) =>
        new(source, source, new(id, 1, DateTimeOffset.Now, [marker], false, false, SpeechEndSample: 32000));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task ExpectCanceled(Task task)
    {
        try { await task; } catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation.");
    }
    private sealed class FakeDetector : ISpeechLanguageDetector
    {
        public bool Disposed;
        public Task<LanguageDetection> DetectAsync(float[] samples, IReadOnlyList<string> languages, CancellationToken token) =>
            Task.FromResult(new LanguageDetection(samples[0] > 0 ? "ja" : "en", .9f));
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeDecoder(bool gpu) : ISpeechDecoder
    {
        public bool UsesGpu => gpu;
        public bool Disposed;
        public string? Language;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<RecognizedPart>> RecognizeAsync(float[] samples, CancellationToken token, string prompt = "", string? fixedLanguage = null)
        { Language = fixedLanguage; Entered.TrySetResult(); await Release.Task.WaitAsync(token); return [new(fixedLanguage!, 0, 1)]; }
        public void Dispose() => Disposed = true;
    }
}
