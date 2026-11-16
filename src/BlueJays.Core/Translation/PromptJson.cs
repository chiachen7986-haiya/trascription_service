using System.Text.Encodings.Web;
using System.Text.Json;

namespace BlueJays.Core.Translation;

/// <summary>JSON embedded in a text prompt must contain literal CJK, not six-character Unicode escapes.</summary>
public static class PromptJson
{
    // This JSON is API data, never embedded in HTML. Quotes and control characters
    // still receive JSON escaping; meeting text remains data in the system prompt.
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
