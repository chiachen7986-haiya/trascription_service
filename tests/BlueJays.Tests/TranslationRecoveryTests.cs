using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BlueJays.Core.Models;
using BlueJays.Core.Translation;

internal static class TranslationRecoveryTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("Japanese correction reserves enough output for CJK, ids and JSON", async () =>
        {
            var requests = 0; var source = new TranslationItem(new string('a', 34), "Mic", "経費精算について確認します。", 1, true);
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                requests++;
                using var payload = await PayloadAsync(request, token);
                Check(payload.RootElement.GetProperty("max_tokens").GetInt32() >= 4096, "The old 512-token cap remains.");
                return Response(JsonSerializer.Serialize(new
                {
                    corrections = new[] { new { id = source.Id, text = source.Text } },
                    translations = new[] { new { id = source.Id, language = "English", text = "We will review expense claims." } }
                }));
            }));
            var result = await new TranslationClient(http).TranslateBatchAsync([source], "[]", English(), new(), "");
            Check(requests == 1 && result.Single().Language == "English", "Japanese translation failed or made unnecessary requests.");
        }
        );
        yield return ("DeepSeek reasoning recovers in fast mode and subsequent clauses start in fast mode", async () =>
        {
            var requests = 0; var notices = new List<string>();
            var settings = new AppSettings { SendThinkingDisabled = false, ContextualCorrectionEnabled = false };
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                requests++;
                using var payload = await PayloadAsync(request, token);
                if (requests == 1)
                {
                    Check(!payload.RootElement.TryGetProperty("thinking", out _), "Did not exercise the user's saved configuration.");
                    return RawResponse(Sse(new { reasoning_content = "Private reasoning must never appear in captions." }, "length"));
                }
                Check(payload.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "disabled", "Recovery did not disable reasoning.");
                Check(payload.RootElement.GetProperty("max_tokens").GetInt32() >= 2048, "Recovery allowance is too small.");
                return Response(Translations("a", "English", "We will check the documents."));
            }));
            var client = new TranslationClient(http); client.Notice += notices.Add;
            var callbacks = new List<TranslationResult>();
            var result = await client.TranslateBatchAsync([Item()], "[]", English(), settings, "", resultReady: callbacks.Add);
            Check(requests == 2 && result.Count == 1 && callbacks.Count == 1 && !callbacks[0].Text.Contains("Private"), "Reasoning recovery lost or leaked output.");
            await client.TranslateBatchAsync([Item()], "[]", English(), settings, "");
            Check(requests == 3 && notices.Any(n => n.Contains("non-thinking")), "Fast-mode recovery was not remembered or explained.");
        }
        );
        yield return ("Truncation retains the final SSE chunk and retries only missing languages with the accepted correction", async () =>
        {
            var requests = 0; var results = new List<TranslationResult>(); var corrections = new List<TranscriptCorrection>();
            const string corrected = "経費精算を確認します。";
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                requests++;
                using var payload = await PayloadAsync(request, token);
                using var input = JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                if (requests == 1)
                {
                    const string prefix = "{\"corrections\":[{\"id\":\"a\",\"text\":\"経費精算を確認します。\"}],\"translations\":[{\"id\":\"a\",\"language\":\"English\",\"text\":\"We will review expense claims.\"},";
                    return RawResponse(Sse(new { content = prefix }, "length"));
                }
                Check(payload.RootElement.GetProperty("max_tokens").GetInt32() == 8192, "Retry did not increase the output allowance.");
                var languages = input.RootElement.GetProperty("target_languages").EnumerateArray().Select(l => l.GetString()).ToArray();
                Check(languages.SequenceEqual(new[] { "French" }), "Already completed English was requested again.");
                Check(input.RootElement.GetProperty("current_captions")[0].GetProperty("text").GetString() == corrected, "Retry discarded the accepted correction.");
                Check(input.RootElement.GetProperty("conversation_history")[0].GetProperty("text").GetString() == "Expense claims", "Retry discarded conversation context.");
                return Response(Translations("a", "French", "Nous vérifierons les notes de frais."));
            }));
            var client = new TranslationClient(http);
            var final = await client.TranslateBatchAsync([Item() with { Text = "経費生産を確認します。" }], "[{\"text\":\"Expense claims\"}]",
                new() { TargetLanguages = ["English", "French"] }, new(), "", resultReady: results.Add, correctionReady: corrections.Add);
            Check(requests == 2 && final.Count == 2 && results.Count == 2 && results[0].Language == "English" && corrections.Count == 1,
                "Partial output was lost, duplicated or re-corrected.");
        }
        );
        yield return ("Complete validated translations survive a length marker without another request", async () =>
        {
            var requests = 0; var delivered = 0;
            using var http = new HttpClient(new Handler((_, _) =>
            {
                requests++;
                // The required object is complete, but the enclosing JSON terminator was cut off.
                const string prefix = "{\"translations\":[{\"id\":\"a\",\"language\":\"English\",\"text\":\"Check the documents.\"}";
                return Task.FromResult(RawResponse(Sse(new { content = prefix }, "length")));
            }));
            var result = await new TranslationClient(http).TranslateBatchAsync([Item()], "[]", English(),
                new() { ContextualCorrectionEnabled = false }, "", resultReady: _ => delivered++);
            Check(requests == 1 && delivered == 1 && result.Single().Text == "Check the documents.", "A completed translation was marked failed or retried.");
        }
        );
        yield return ("Repeated truncation is bounded and reports a useful output-limit error", async () =>
        {
            var requests = 0;
            using var http = new HttpClient(new Handler((_, _) =>
            {
                requests++; return Task.FromResult(RawResponse(Sse(new { content = "{\"translations\":[" }, "length")));
            }));
            try
            {
                await new TranslationClient(http).TranslateBatchAsync([Item()], "[]", English(), new() { ContextualCorrectionEnabled = false }, "");
                throw new Exception("Incomplete output was accepted.");
            }
            catch (Exception ex) when (ex.Message.Contains("after recovery"))
            { Check(requests == 2 && ex.Message.Contains("non-thinking"), "Retry was unbounded or error remained misleading."); }
        }
        );
        yield return ("Generic providers never receive a DeepSeek extension during truncation recovery", async () =>
        {
            var requests = 0;
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                requests++; using var payload = await PayloadAsync(request, token);
                Check(!payload.RootElement.TryGetProperty("thinking", out _), "Injected a proprietary parameter into a generic provider.");
                return requests == 1 ? RawResponse(Sse(new { reasoning_content = "Provider reasoning" }, "length")) : Response(Translations("a", "English", "Documents."));
            }));
            await new TranslationClient(http).TranslateBatchAsync([Item()], "[]", English(),
                new() { ApiBaseUrl = "http://localhost:1234/v1", SendThinkingDisabled = false, ContextualCorrectionEnabled = false }, "");
            Check(requests == 2, "Generic recovery did not finish.");
        }
        );
        yield return ("Cancellation stops output-limit recovery without starting more requests", async () =>
        {
            var requests = 0; using var stop = new CancellationTokenSource();
            using var http = new HttpClient(new Handler(async (_, token) =>
            {
                requests++;
                if (requests == 1) return RawResponse(Sse(new { content = "{" }, "length"));
                stop.Cancel(); await Task.Delay(Timeout.Infinite, token); throw new Exception("Cancellation ignored.");
            }));
            try
            {
                await new TranslationClient(http).TranslateBatchAsync([Item()], "[]", English(), new() { ContextualCorrectionEnabled = false }, "", stop.Token);
                throw new Exception("Cancellation ignored.");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { Check(requests == 2, "Started another request after cancellation."); }
        }
        );
    }

    private static TranslationItem Item() => new("a", "Mic", "資料を確認します。", 1, true);
    private static ConversationOptions English() => new() { SpokenLanguages = ["ja"], TargetLanguages = ["English"] };
    // Separate asynchronous reading keeps the document lifetime inside each handler.
    private static async Task<JsonDocument> PayloadAsync(HttpRequestMessage request, CancellationToken token) => JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
    private static string Translations(string id, string language, string text) => JsonSerializer.Serialize(new { translations = new[] { new { id, language, text } } });
    private static string Sse(object delta, string? finish = null) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta, finish_reason = finish } } }) + "\n\n";
    private static HttpResponseMessage RawResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
    private static HttpResponseMessage Response(string json) => RawResponse(Sse(new { content = json }, "stop"));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handle(request, token); }
}
