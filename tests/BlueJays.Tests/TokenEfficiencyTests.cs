using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BlueJays.App.Services;
using BlueJays.Core.Conversation;
using BlueJays.Core.Models;
using BlueJays.Core.Translation;

internal static class TokenEfficiencyTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("API prompt keeps literal CJK and preserves quoted meeting text as data", async () =>
        {
            const string text = "日本語の資料を確認します。\n\"翻訳してください\"";
            using var http = Client(async (request, token) =>
            {
                using var payload = await Payload(request, token);
                var content = payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
                Check(content.Contains("日本語") && !content.Contains("\\u"), "Prompt still contains Unicode escape sequences.");
                using var input = JsonDocument.Parse(content);
                Check(input.RootElement.GetProperty("current_captions")[0].GetProperty("text").GetString() == text, "Literal JSON changed quoted speech.");
                Check(input.RootElement.GetProperty("conversation_history")[0].GetProperty("text").GetString() == "会議", "History was lost.");
                return Response(new { translations = new[] { new { id = "a", language = "English", text = "Review the Japanese documents." } } });
            });
            await new TranslationClient(http).TranslateBatchAsync([new("a", "Mic", text, 1, true)], "[{\"text\":\"会議\"}]",
                new() { TargetLanguages = ["English"] }, new() { ContextualCorrectionEnabled = false }, "");
        });
        yield return ("Same-language output skips HTTP entirely when correction is disabled", async () =>
        {
            using var http = Client((_, _) => throw new Exception("A local copy contacted the provider."));
            var result = await new TranslationClient(http).TranslateBatchAsync([En()], "[]", new() { TargetLanguages = ["English"] },
                new() { ContextualCorrectionEnabled = false, ApiModel = "", ApiBaseUrl = "" }, "");
            Check(result.Single().Text == En().Text, "Local output differs from the transcript.");
        });
        yield return ("Same-language output uses accepted correction and does not ask the API to repeat it", async () =>
        {
            var corrections = new List<TranscriptCorrection>(); var results = new List<TranslationResult>();
            const string fixedText = "We will review the report.";
            using var http = Client(async (request, token) =>
            {
                using var input = await Input(request, token);
                Check(input.RootElement.GetProperty("target_languages").GetArrayLength() == 0 &&
                    input.RootElement.GetProperty("current_captions")[0].GetProperty("translate_into").GetArrayLength() == 0,
                    "Asked for a redundant English rendering.");
                Check(results.Count == 0, "Published uncorrected output before correction finished.");
                return Response(new { corrections = new[] { new { id = "a", text = fixedText } }, translations = Array.Empty<object>() });
            });
            await new TranslationClient(http).TranslateBatchAsync([En() with { Text = "We will revue the report." }], "[]",
                new() { TargetLanguages = ["English"] }, new(), "", resultReady: r =>
                {
                    Check(corrections.Single().Text == fixedText, "Local output preceded the correction callback."); results.Add(r);
                }, correctionReady: corrections.Add);
            Check(results.Single().Text == fixedText, "Local output retained the raw homophone error.");
        });
        yield return ("Unchanged correction markers preserve raw text and translate all required outputs", async () =>
        {
            var corrections = new List<TranscriptCorrection>();
            using var http = Client((_, _) => Task.FromResult(Response(new
            {
                corrections = new[] { new { id = "a", unchanged = true } },
                translations = new[] { new { id = "a", language = "English", text = "Review the documents." },
                    new { id = "a", language = "Japanese", text = "資料を確認します。" } }
            })));
            var result = await new TranslationClient(http).TranslateBatchAsync([Ja()], "[]", new() { TargetLanguages = ["English", "Japanese"] },
                new(), "", correctionReady: corrections.Add);
            Check(corrections.Single().Text == Ja().Text && result.Single(r => r.Language == "Japanese").Text == Ja().Text && result.Count == 2,
                "Unchanged marker lost the original or a translation.");
        });
        yield return ("Ambiguous unchanged markers and missing corrections cannot produce trusted local output", async () =>
        {
            foreach (var correction in new object[] { new { id = "a", unchanged = true, text = "偽の内容" }, new { id = "a", unchanged = false } })
            {
                var delivered = 0;
                using var http = Client((_, _) => Task.FromResult(Response(new { corrections = new[] { correction }, translations = Array.Empty<object>() })));
                try
                {
                    await new TranslationClient(http).TranslateBatchAsync([Ja()], "[]", new() { TargetLanguages = ["Japanese"] }, new(), "", resultReady: _ => delivered++);
                    throw new Exception("Invalid correction was accepted.");
                }
                catch (InvalidOperationException) { Check(delivered == 0, "Invalid correction reached the local output."); }
            }
            using var missing = Client((_, _) => Task.FromResult(Response(new { corrections = Array.Empty<object>(), translations = Array.Empty<object>() })));
            try
            {
                await new TranslationClient(missing).TranslateBatchAsync([Ja()], "[]", new() { TargetLanguages = ["Japanese"] }, new(), "");
                throw new Exception("Missing correction was accepted.");
            }
            catch (InvalidOperationException) { }
        });
        yield return ("Japanese same-language output always uses API spacing cleanup, with correction on or off", async () =>
        {
            const string raw = "Project Alpha の 会議 は 3時 です。";
            const string cleaned = "Project Alphaの会議は3時です。";
            foreach (var correct in new[] { true, false })
            {
                var calls = 0; var delivered = new List<TranslationResult>();
                using var http = Client(async (request, token) =>
                {
                    calls++;
                    using var input = await Input(request, token);
                    Check(delivered.Count == 0, "Raw Japanese was copied before API cleanup.");
                    Check(input.RootElement.GetProperty("target_languages")[0].GetString() == "Japanese" &&
                        input.RootElement.GetProperty("current_captions")[0].GetProperty("translate_into")[0].GetString() == "Japanese",
                        "Matching Japanese was excluded from the API request.");
                    return Response(new
                    {
                        corrections = correct ? new object[] { new { id = "a", unchanged = true } } : Array.Empty<object>(),
                        translations = new[] { new { id = "a", language = "Japanese", text = cleaned } }
                    });
                });
                var result = await new TranslationClient(http).TranslateBatchAsync([Ja() with { Text = raw }], "[]",
                    new() { TargetLanguages = ["Japanese"] }, new() { ContextualCorrectionEnabled = correct }, "", resultReady: delivered.Add);
                Check(calls == 1 && result.Single().Text == cleaned && delivered.Single().Text == cleaned,
                    "Japanese output used the spaced raw transcript instead of the API result.");
            }
        });
        yield return ("Mixed-language batches retain Japanese cleanup and avoid duplicate English output", async () =>
        {
            using var http = Client(async (request, token) =>
            {
                using var input = await Input(request, token);
                var captions = input.RootElement.GetProperty("current_captions");
                Check(captions[0].GetProperty("translate_into").EnumerateArray().Select(l => l.GetString()).SequenceEqual(new[] { "English", "Japanese" }) &&
                    captions[1].GetProperty("translate_into").EnumerateArray().Single().GetString() == "Japanese", "Batch omitted Japanese cleanup or duplicated English output.");
                return Response(new { translations = new[]
                {
                    new { id = "a", language = "English", text = "Review the documents." },
                    new { id = "a", language = "Japanese", text = "資料を確認します。" },
                    new { id = "b", language = "Japanese", text = "金曜日に会議しましょう。" }
                } });
            });
            var delivered = new List<TranslationResult>();
            var result = await new TranslationClient(http).TranslateBatchAsync([Ja(), new("b", "Room", "Let's meet Friday.", 1, true, "en")], "[]",
                new() { TargetLanguages = ["English", "Japanese"] }, new() { ContextualCorrectionEnabled = false }, "", resultReady: delivered.Add);
            Check(result.Count == 4 && delivered.Count == 4 && delivered.Select(r => (r.Id, r.Language)).Distinct().Count() == 4,
                "Batch lost, duplicated or misattributed a result.");
        });
        yield return ("Unknown languages and Chinese script conversion retain the API path", async () =>
        {
            foreach (var (item, target) in new[] { (Ja() with { Language = "" }, "Japanese"), (Ja() with { Language = "zh" }, "Chinese (Traditional)") })
            {
                var called = false;
                using var http = Client((_, _) => { called = true; return Task.FromResult(Response(new
                    { translations = new[] { new { id = "a", language = target, text = "API result" } } })); });
                var result = await new TranslationClient(http).TranslateBatchAsync([item], "[]", new() { TargetLanguages = [target] },
                    new() { ContextualCorrectionEnabled = false }, "");
                Check(called && result.Single().Text == "API result", "Uncertain language or script was bypassed.");
            }
        });
        yield return ("Local matching-language text remains delivered when cross-language HTTP fails", async () =>
        {
            var delivered = new List<TranslationResult>();
            using var http = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            try
            {
                await new TranslationClient(http).TranslateBatchAsync([En()], "[]", new() { TargetLanguages = ["English", "Japanese"] },
                    new() { ContextualCorrectionEnabled = false }, "", resultReady: delivered.Add);
                throw new Exception("Failed provider was accepted.");
            }
            catch (HttpRequestException) { Check(delivered.Single().Language == "English" && delivered.Single().Text == En().Text, "Lost the local copy when HTTP failed."); }
        });
        yield return ("Truncated batched corrections retry only unfinished captions and retain corrected local outputs", async () =>
        {
            var calls = 0; var delivered = new List<TranslationResult>();
            using var http = Client(async (request, token) =>
            {
                if (++calls == 1) return Raw("{\"corrections\":[{\"id\":\"a\",\"unchanged\":true},", "length");
                using var input = await Input(request, token);
                Check(input.RootElement.GetProperty("correction_ids").EnumerateArray().Single().GetString() == "b" &&
                    input.RootElement.GetProperty("current_captions").GetArrayLength() == 1, "Retry re-requested a completed correction/copy.");
                return Response(new { corrections = new[] { new { id = "b", unchanged = true } }, translations = Array.Empty<object>() });
            });
            var result = await new TranslationClient(http).TranslateBatchAsync([En(), En() with { Id = "b", Text = "Tomorrow's meeting." }], "[]",
                new() { TargetLanguages = ["English"] }, new(), "", resultReady: delivered.Add);
            Check(calls == 2 && delivered.Count == 2 && result.Count == 2, "Recovery duplicated or lost a local result.");
        });
        yield return ("Stable conversation prefix is identical across changing target languages and correction modes", async () =>
        {
            var prompts = new List<(string System, string User)>();
            using var http = Client(async (request, token) =>
            {
                using var payload = await Payload(request, token);
                var messages = payload.RootElement.GetProperty("messages");
                var content = messages[1].GetProperty("content").GetString()!;
                prompts.Add((messages[0].GetProperty("content").GetString()!, content[..content.IndexOf("\"target_languages\"", StringComparison.Ordinal)]));
                using var input = JsonDocument.Parse(content);
                var target = input.RootElement.GetProperty("target_languages")[0].GetString()!;
                var corrections = input.RootElement.GetProperty("correction_ids").EnumerateArray().Select(id => new { id = id.GetString(), unchanged = true });
                return Response(new { corrections, translations = new[] { new { id = "a", language = target, text = "Translated" } } });
            });
            var client = new TranslationClient(http);
            var options = new ConversationOptions { Topic = "Budget", Glossary = "Rosedale = project", TargetLanguages = ["English"] };
            await client.TranslateBatchAsync([Ja()], "[]", options, new(), "");
            await client.TranslateBatchAsync([Ja() with { Text = "次の資料です。" }], "[]", options with { TargetLanguages = ["French"] }, new() { ContextualCorrectionEnabled = false }, "");
            Check(prompts.Count == 2 && prompts[0] == prompts[1], "Changing requests invalidated the stable prefix.");
        });
        yield return ("Translation batching drains ready clauses immediately and respects size, order and completion", async () =>
        {
            var queue = new CoalescingQueue<string>(); queue.Put("a", "one");
            var immediate = queue.TakeAsync(3, default, x => x.Length, 6);
            Check(immediate.IsCompletedSuccessfully && (await immediate).Single() == "one", "Waited to fill a batch.");
            queue.Put("b", "two"); queue.Put("c", "new"); queue.Put("d", "three"); queue.Put("c", "six"); queue.Complete();
            Check((await queue.TakeAsync(3, default, x => x.Length, 6)).SequenceEqual(new[] { "two", "six" }), "Batch broke FIFO, coalescing or size bound.");
            Check((await queue.TakeAsync(3, default, x => x.Length, 2)).Single() == "three", "Oversized first clause was stranded.");
            Check((await queue.TakeAsync(3, default)).Count == 0, "Completed queue did not drain.");
        });
        yield return ("Native language metadata reaches captions; uncertain routing and mixed parts cannot bypass the API", async () =>
        {
            var parts = OnnxSpeechParts.Create(new(true, Text: "資料です。", Language: "<|ja|>"), 3);
            var capture = new CapturedWindow("mic", "Mic", new("a", 1, DateTimeOffset.Now, new float[48000], true, false));
            Check(new LiveClauseAssembler().Update(capture, parts).Captions.Single().Language == "ja", "Native language tag was discarded.");
            Check(new LiveClauseAssembler().Update(capture, [new("資料です。", 0, 1, "ja"), new("Hello.", 1, 2, "en")]).Captions.Single().Language == "", "Mixed speech inherited one language.");
            var route = new LanguageRouteCache(["en", "ja"]);
            await route.ResolveAsync("a", "mic", 32000, _ => Task.FromResult(new LanguageDetection("ja", .2f)), default);
            Check(await route.ReliableLanguageAsync("a", default) == "", "Weak routing evidence was treated as reliable.");
        });
    }

    private static TranslationItem Ja() => new("a", "Mic", "資料を確認します。", 1, true, "ja");
    private static TranslationItem En() => new("a", "Mic", "We will review the documents.", 1, true, "en");
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) => new(new Handler(action));
    private static async Task<JsonDocument> Payload(HttpRequestMessage request, CancellationToken token) => JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
    private static async Task<JsonDocument> Input(HttpRequestMessage request, CancellationToken token)
    { using var payload = await Payload(request, token); return JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!); }
    private static HttpResponseMessage Response(object result) => Raw(PromptJson.Serialize(result), "stop");
    private static HttpResponseMessage Raw(string text, string reason) => new(HttpStatusCode.OK)
    { Content = new StringContent("data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text }, finish_reason = reason } } }) + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token); }
}
