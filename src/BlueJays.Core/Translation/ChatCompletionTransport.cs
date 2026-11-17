using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BlueJays.Core.Models;

namespace BlueJays.Core.Translation;

/// <summary>Shared streaming transport for translations and on-demand export summaries.</summary>
public static class ChatCompletionTransport
{
    public static async Task<string> CompleteAsync(HttpClient http, Dictionary<string, object> payload,
        AppSettings settings, string apiKey, CancellationToken token, Action<string>? textReceived = null,
        bool interruptReasoning = false, Action<ApiTokenUsage?>? usageReceived = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        if (settings.SendThinkingDisabled) payload["thinking"] = new { type = "disabled" };
        var endpoint = TranslationClient.GetEndpoint(settings.ApiBaseUrl);
        if (usageReceived is not null && endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase))
            payload["stream_options"] = new { include_usage = true };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}: check the API endpoint, model, key and quota.");
        ApiTokenUsage? usage = null;
        try { return await ReadStreamAsync(); }
        finally { usageReceived?.Invoke(usage); }

        async Task<string> ReadStreamAsync()
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
            var output = new StringBuilder();
            var truncated = false;
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                if (data.Length == 0) continue;
                using var document = JsonDocument.Parse(data);
                var chunk = document.RootElement;
                if (chunk.TryGetProperty("error", out _)) throw new HttpRequestException("The API returned a streaming error.");
                usage = ApiTokenUsage.Read(chunk) ?? usage;
                if (truncated)
                {
                    if (usage is not null) break;
                    continue;
                }
                if (!chunk.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                if (choices[0].TryGetProperty("delta", out var delta))
                {
                    if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString()!;
                        output.Append(text);
                        textReceived?.Invoke(text);
                    }
                    // Stop hidden reasoning early so recovery can continue within the live-caption deadline.
                    if (interruptReasoning && delta.TryGetProperty("reasoning_content", out var reasoning) &&
                        reasoning.ValueKind == JsonValueKind.String && reasoning.GetString()!.Length > 0)
                        throw new CompletionReasoningDetectedException();
                }
                // Deliver final content first and allow a trailing usage event on truncated responses.
                if (choices[0].TryGetProperty("finish_reason", out var reason) && reason.GetString() == "length")
                {
                    truncated = true;
                    if (usageReceived is null || usage is not null) break;
                }
                if (output.Length > 120_000) throw new InvalidOperationException("The API response exceeded the output limit.");
            }
            if (truncated) throw new CompletionTruncatedException();
            if (output.Length == 0) throw new InvalidOperationException("The API returned no text. Check streaming support and the model ID.");
            return output.ToString();
        }
    }

    public static string RemoveJsonFence(string text)
    {
        text = text.Trim();
        return text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal)
            ? text[(text.IndexOf('\n') + 1)..^3].Trim() : text;
    }
}
