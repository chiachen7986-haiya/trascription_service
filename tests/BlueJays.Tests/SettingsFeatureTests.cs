using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using BlueJays.App.Services;
using BlueJays.Core.Models;
using BlueJays.Core.Translation;

internal static class SettingsFeatureTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("Dictionaries survive restart, round-trip readings, and preserve existing glossaries", () => InTemporaryDirectory(folder =>
        {
            var store = new TerminologyStore(folder);
            var dictionary = new TerminologyDictionary(Guid.NewGuid(), "Project vocabulary",
                [new("経費精算", "ja", "けいひせいさん", "expense claims"), new("WHP", "en", Meaning: "Project acronym")]);
            store.Save(dictionary);
            var reloaded = new TerminologyStore(folder).Load().Single();
            Check(reloaded.Entries.SequenceEqual(dictionary.Entries), "Saved dictionary entries did not survive reload.");
            var path = Path.Combine(folder, "shared.json");
            store.Export(path, reloaded);
            var imported = store.Import(path);
            Check(imported.Id != reloaded.Id && imported.Name != reloaded.Name &&
                imported.Entries.SequenceEqual(reloaded.Entries), "Import modified the original or lost term data.");
            var options = JsonSerializer.Deserialize<ConversationOptions>("""{"Glossary":"Original glossary"}""")!;
            var effective = reloaded.ApplyTo(options);
            Check(options.TerminologyDictionaryId is null && effective.Glossary.Contains("Original glossary") &&
                effective.Glossary.Contains("経費精算 [ja] (けいひせいさん) = expense claims") &&
                effective.RecognitionGlossary == "Original glossary\n経費精算\nWHP", "Dictionary did not reach the existing prompt inputs.");
            File.WriteAllText(path, """{"version":2,"dictionaries":[]}""");
            try { store.Import(path); throw new Exception("Invalid import was accepted."); }
            catch (InvalidDataException) { }
            Check(store.Load().Length == 2, "Failed import altered the library.");
            store.Delete(imported.Id);
            Check(new TerminologyStore(folder).Load().Single().Id == dictionary.Id, "Deleting the import removed another dictionary.");
        }));

        yield return ("Usage counts survive concurrent writes and restart, and quotes price cached tokens once", () => InTemporaryDirectory(folder =>
        {
            var store = new TokenUsageStore(folder);
            var today = new DateOnly(2026, 11, 17);
            Parallel.For(0, 8, _ => store.Record(new(100, 30, 10), today));
            store.Record(null, today);
            store.Record(new(20, 0, 2), today.AddDays(-1));
            var (days, warning) = new TokenUsageStore(folder).Snapshot();
            Check(!warning && days.Length == 2 && days.Single(day => day.Day == today) == new DailyTokenUsage(today, 800, 240, 80, 1),
                "Usage history did not preserve daily totals and responses with no counts.");
            var peak = ApiPricing.Models.Single(price => price.Model == "deepseek-flash (peak)");
            Check(peak.Quote(new(1_000_000, 500_000, 250_000)) == 0.453m, "Cached input was billed twice or used the wrong rate.");
        }));

        yield return ("Streaming usage counts final events once, including truncated OpenAI and DeepSeek responses", async () =>
        {
            var usage = new List<ApiTokenUsage?>();
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var openAi = request.RequestUri!.Host == "api.openai.com";
                Check(payload.RootElement.TryGetProperty("stream_options", out var streamOptions) == openAi &&
                    (!openAi || streamOptions.GetProperty("include_usage").GetBoolean()), "Usage option was sent to the wrong API.");
                var body = request.RequestUri.Host switch
                {
                    "api.openai.com" => """
                        data: {"choices":[{"delta":{"content":"a"},"finish_reason":"length"}],"usage":null}
                        data: {"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":50}}}
                        data: [DONE]
                        """,
                    "api.deepseek.com" => """
                        data: {"choices":[{"delta":{"content":"a"},"finish_reason":"stop"}],"usage":{"prompt_tokens":20,"completion_tokens":6,"prompt_cache_hit_tokens":8}}
                        data: [DONE]
                        """,
                    _ => """
                        data: {"choices":[{"delta":{"content":"a"},"finish_reason":"stop"}]}
                        data: [DONE]
                        """
                };
                return new(HttpStatusCode.OK) { Content = new StringContent(body) };
            }));
            foreach (var host in new[] { "api.openai.com", "api.deepseek.com", "localhost" })
            {
                try
                {
                    await ChatCompletionTransport.CompleteAsync(http, new() { ["stream"] = true },
                        new() { ApiBaseUrl = $"https://{host}/v1", SendThinkingDisabled = false }, "", CancellationToken.None,
                        usageReceived: usage.Add);
                    Check(host != "api.openai.com", "The length finish reason was lost.");
                }
                catch (Exception ex) when (host == "api.openai.com" && ex.Message == "The API reached its output limit before finishing.") { }
            }
            Check(usage.SequenceEqual(new ApiTokenUsage?[] { new(100, 50, 20), new(20, 8, 6), null }),
                "Usage totals were missing, counted twice or treated as zero when absent.");
        });

        yield return ("Mistral streamed translation requests usage and persists the returned counts", async () =>
        {
            var directory = Directory.CreateTempSubdirectory("BlueJays-mistral-usage-");
            try
            {
                var store = new TokenUsageStore(directory.FullName);
                using var http = new HttpClient(new Handler(async (request, token) =>
                {
                    using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    var includeUsage = payload.RootElement.TryGetProperty("stream_options", out var options) &&
                        options.GetProperty("include_usage").GetBoolean();
                    var completion = JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { delta = new { content = """{"translations":[{"id":"one","language":"English","text":"Good morning."}]}""" },
                            finish_reason = "stop" } }
                    });
                    const string usage = """{"choices":[],"usage":{"prompt_tokens":130,"completion_tokens":32,"prompt_tokens_details":{"cached_tokens":64}}}""";
                    var events = $"data: {completion}\n\n" + (includeUsage ? $"data: {usage}\n\n" : "") + "data: [DONE]\n\n";
                    return new(HttpStatusCode.OK) { Content = new StringContent(events) };
                }));
                var settings = new AppSettings
                {
                    ApiBaseUrl = "https://api.mistral.ai/v1", ApiModel = "mistral-small-latest",
                    SendThinkingDisabled = false, ContextualCorrectionEnabled = false
                };
                var translation = await new TranslationClient(http, store.Record).TranslateBatchAsync(
                    [new("one", "Mic", "おはようございます。", 1, true, "ja")], "[]",
                    new() { TargetLanguages = ["English"] }, settings, "");
                var day = new TokenUsageStore(directory.FullName).Snapshot().Days.Single();
                Check(translation.Single().Text == "Good morning." && day is { Input: 130, CachedInput: 64, Output: 32, Unreported: 0 },
                    "Mistral translated the caption but its API usage was not recorded.");
            }
            finally { directory.Delete(recursive: true); }
        });
    }

    private static Task InTemporaryDirectory(Action<string> action)
    {
        var directory = Directory.CreateTempSubdirectory("BlueJays-settings-");
        try { action(directory.FullName); }
        finally { directory.Delete(recursive: true); }
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
