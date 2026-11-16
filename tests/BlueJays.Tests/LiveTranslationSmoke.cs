using System.Diagnostics;
using System.Net.Http;
using BlueJays.App.Services;
using BlueJays.Core.Models;
using BlueJays.Core.Translation;

internal static class LiveTranslationSmoke
{
    public static async Task<int> TokenEfficiencyAsync()
    {
        var saved = new SettingsStore().Load();
        if (TranslationClient.GetEndpoint(saved.Settings.ApiBaseUrl).Host != "api.deepseek.com" || saved.ApiKey.Length == 0)
            throw new InvalidOperationException("This diagnostic requires a configured official DeepSeek endpoint and saved API key.");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var client = new TranslationClient(http);
        var corrections = new Dictionary<string, string>();
        var result = await client.TranslateBatchAsync([
            new("synthetic-ja", "Synthetic Japanese", "明日の 会議は 午後3時です。田中さんが 資料を 準備します。", 1, true, "ja"),
            new("synthetic-en", "Synthetic English", "Yes, please send the documents before the meeting.", 1, true, "en")],
            "[]", new() { SpokenLanguages = ["ja", "en"], TargetLanguages = ["English", "Japanese"], Topic = "Meeting planning", Glossary = "田中 = Tanaka" },
            saved.Settings with { ContextualCorrectionEnabled = true, SendThinkingDisabled = true }, saved.ApiKey,
            correctionReady: correction => corrections.Add(correction.Id, correction.Text));
        var japanese = result.Single(r => r.Id == "synthetic-ja" && r.Language == "Japanese").Text;
        foreach (var translation in result) Console.WriteLine($"{translation.Id} → {translation.Language}: {translation.Text}");
        if (result.Count != 4 || corrections.Count != 2 || string.IsNullOrWhiteSpace(japanese) || japanese.Contains("の 会議") || japanese.Contains("資料を 準備") ||
            result.Single(r => r.Id == "synthetic-en" && r.Language == "English").Text != corrections["synthetic-en"])
            throw new InvalidOperationException("Batched live translation lost a correction or matching-language output.");
        Console.WriteLine("PASS live bilingual batch: Japanese spacing cleanup, three API outputs and one corrected English local copy.");
        return 0;
    }

    /// <summary>Explicit opt-in network diagnostic. Uses only synthetic text; never prints credentials.</summary>
    public static async Task<int> RunAsync()
    {
        var saved = new SettingsStore().Load();
        if (TranslationClient.GetEndpoint(saved.Settings.ApiBaseUrl).Host != "api.deepseek.com" || saved.ApiKey.Length == 0)
            throw new InvalidOperationException("This diagnostic requires a configured official DeepSeek endpoint and saved API key.");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var client = new TranslationClient(http);
        client.Notice += Console.WriteLine;
        var watch = Stopwatch.StartNew();
        var result = await client.TranslateBatchAsync(
            [new("synthetic-smoke", "Synthetic test", "明日の会議は午後3時です。田中さんが資料を準備します。", 1, true)],
            "[]", new ConversationOptions { SpokenLanguages = ["ja"], TargetLanguages = ["English"], Topic = "Meeting planning", Glossary = "田中 = Tanaka" },
            saved.Settings, saved.ApiKey);
        var english = result.Single(r => r.Language == "English").Text;
        if (string.IsNullOrWhiteSpace(english)) throw new InvalidOperationException("The live test returned no English translation.");
        Console.WriteLine($"PASS live Japanese → English in {watch.Elapsed.TotalSeconds:F2} s: {english}");
        return 0;
    }
}
