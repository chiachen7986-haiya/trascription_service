namespace BlueJays.Core.Conversation;

public sealed record LanguageDetection(string Language, float Probability);

/// <summary>Language detection is shared by previews, rather than repeated inside every STT pass.</summary>
public sealed class LanguageRouteCache(IReadOnlyList<string> allowedLanguages)
{
    private sealed record Route(string Language, long CheckedThrough, float Confidence, bool Reliable);
    private readonly string[] languages = allowedLanguages.Where(l => l != "auto").Distinct().ToArray();
    private readonly Dictionary<string, Route> utterances = new();
    private readonly Dictionary<string, string> sourceLanguages = new();
    private readonly SemaphoreSlim gate = new(1);

    public async Task<string> ResolveAsync(string utteranceId, string sourceId, long speechEndSample,
        Func<CancellationToken, Task<LanguageDetection>> detect, CancellationToken token)
    {
        if (languages.Length == 1) return languages[0];
        await gate.WaitAsync(token);
        try
        {
            utterances.TryGetValue(utteranceId, out var prior);
            // Revisit a long turn after six new seconds, or sooner after weak evidence.
            var recheckAfter = (prior?.Confidence >= .5f ? 6 : 2) * 16000L;
            if (prior is not null && speechEndSample - prior.CheckedThrough < recheckAfter) return prior.Language;
            var detection = await detect(token); token.ThrowIfCancellationRequested();
            var valid = detection.Language.Length > 0 && (languages.Length == 0 || languages.Contains(detection.Language));
            var selected = valid ? detection.Language : prior?.Language ?? sourceLanguages.GetValueOrDefault(sourceId) ?? languages.FirstOrDefault() ?? "en";
            // Weak short-window evidence should not flip a known source's language.
            if (detection.Probability < .5f && (prior?.Language ?? sourceLanguages.GetValueOrDefault(sourceId)) is { } previous)
                selected = previous;
            utterances[utteranceId] = new(selected, speechEndSample, detection.Probability,
                valid && selected == detection.Language && detection.Probability >= .5f);
            if (detection.Probability >= .5f && valid) sourceLanguages[sourceId] = selected;
            return selected;
        }
        finally { gate.Release(); }
    }

    public async Task<string> ReliableLanguageAsync(string utteranceId, CancellationToken token)
    {
        if (languages.Length == 1) return languages[0];
        await gate.WaitAsync(token);
        try { return utterances.TryGetValue(utteranceId, out var route) && route.Reliable ? route.Language : ""; }
        finally { gate.Release(); }
    }

    public async Task ForgetAsync(string utteranceId)
    {
        await gate.WaitAsync();
        try { utterances.Remove(utteranceId); }
        finally { gate.Release(); }
    }
}
