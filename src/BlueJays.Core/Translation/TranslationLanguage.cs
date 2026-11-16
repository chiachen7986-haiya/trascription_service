namespace BlueJays.Core.Translation;

public static class TranslationLanguage
{
    public static string Normalize(string language) => language.Trim().ToLowerInvariant().Replace("<|", "").Replace("|>", "") switch
    {
        "english" or "en" => "en", "japanese" or "ja" => "ja",
        "chinese" or "zh" => "zh", "korean" or "ko" => "ko", "cantonese" or "yue" => "yue",
        "french" or "fr" => "fr", "spanish" or "es" => "es", "german" or "de" => "de",
        "italian" or "it" => "it", "portuguese" or "pt" => "pt", "russian" or "ru" => "ru",
        "arabic" or "ar" => "ar", "hindi" or "hi" => "hi", "thai" or "th" => "th",
        "vietnamese" or "vi" => "vi", "indonesian" or "id" => "id", "dutch" or "nl" => "nl",
        "polish" or "pl" => "pl", "ukrainian" or "uk" => "uk", "turkish" or "tr" => "tr",
        "swedish" or "sv" => "sv", "finnish" or "fi" => "fi", "danish" or "da" => "da", "norwegian" or "no" => "no",
        // A spoken-language tag does not specify Chinese writing conventions.
        // Keep script conversion on the API unless the script is explicitly known.
        "chinese (traditional)" or "zh-hant" => "zh-hant",
        "chinese (simplified)" or "zh-hans" => "zh-hans", _ => ""
    };

    public static bool Matches(string source, string target) => Normalize(source) is { Length: > 0 } code && code == Normalize(target);

    // Japanese STT often contains unnatural word spacing. Retain an API-rendered
    // Japanese output even when the source language matches, as in the original release.
    public static bool CanCopyLocally(string source, string target) => Normalize(target) != "ja" && Matches(source, target);
}
