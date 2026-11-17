using System.Text.Json;

namespace BlueJays.Core.Translation;

public readonly record struct ApiTokenUsage(long Input, long CachedInput, long Output)
{
    public long Total => Input + Output;

    internal static ApiTokenUsage? Read(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        var input = Count(usage, "prompt_tokens");
        var output = Count(usage, "completion_tokens");
        if (input is null || output is null || input > long.MaxValue - output) return null;
        var cached = Count(usage, "prompt_cache_hit_tokens") ?? Count(usage, "cached_tokens");
        if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
            cached ??= Count(details, "cached_tokens");
        return new(input.Value, Math.Min(input.Value, cached ?? 0), output.Value);
    }

    private static long? Count(JsonElement json, string property) =>
        json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var count) && count >= 0 ? count : null;
}
