namespace BlueJays.Core.Translation;

public sealed record ApiModelPrice(string Provider, string Model, decimal Input, decimal CachedInput, decimal Output, string Source)
{
    public decimal Quote(ApiTokenUsage usage)
    {
        if (usage.Input < 0 || usage.Output < 0 || usage.CachedInput < 0 || usage.CachedInput > usage.Input)
            throw new ArgumentOutOfRangeException(nameof(usage));
        return ((usage.Input - usage.CachedInput) * Input + usage.CachedInput * CachedInput + usage.Output * Output) / 1_000_000m;
    }
}

public static class ApiPricing
{
    public static DateOnly VerifiedOn { get; } = new(2026, 11, 17);
    private const string OpenAi = "https://developers.openai.com/api/docs/pricing";
    private const string Mistral = "https://docs.mistral.ai/inference/pricing";
    private const string DeepSeek = "https://api-docs.deepseek.com/quick_start/pricing/";

    public static IReadOnlyList<ApiModelPrice> Models { get; } = Array.AsReadOnly<ApiModelPrice>(
    [
        new("OpenAI", "gpt-6-astra", 10m, 1m, 50m, OpenAi),
        new("OpenAI", "gpt-6.1-sol", 2m, 0.10m, 10m, OpenAi),
        new("OpenAI", "gpt-6.1-terra", 1m, 0.05m, 5m, OpenAi),
        new("OpenAI", "gpt-6-luna", 0.10m, 0.01m, 0.50m, OpenAi),
        new("OpenAI", "gpt-5.4-mini", 0.75m, 0.075m, 4.50m, "https://developers.openai.com/api/docs/models/gpt-5.4-mini"),
        new("Mistral AI", "Mistral Large 4", 0.70m, 0.07m, 2.10m, "https://docs.mistral.ai/models/mistral-large"),
        new("Mistral AI", "Mistral Large 3", 0.50m, 0.05m, 1.50m, Mistral),
        new("Mistral AI", "Mistral Medium 3.5", 1.50m, 0.15m, 7.50m, Mistral),
        new("Mistral AI", "Mistral Small 4", 0.15m, 0.015m, 0.60m, Mistral),
        new("DeepSeek", "deepseek-flash (off-peak)", 0.15m, 0.003m, 0.60m, DeepSeek),
        new("DeepSeek", "deepseek-flash (peak)", 0.30m, 0.006m, 1.20m, DeepSeek),
        new("DeepSeek", "deepseek-v4-pro (off-peak)", 0.66m, 0.022m, 1.98m, DeepSeek),
        new("DeepSeek", "deepseek-v4-pro (peak)", 1.32m, 0.044m, 3.96m, DeepSeek)
    ]);
}
