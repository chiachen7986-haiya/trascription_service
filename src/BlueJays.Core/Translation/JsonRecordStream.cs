using System.Text;
using System.Text.Json;

namespace BlueJays.Core.Translation;

/// <summary>Emits complete array objects as SSE text arrives, including split escapes and CJK text.</summary>
public sealed class JsonRecordStream(Action<string, JsonElement> recordReady)
{
    private readonly StringBuilder buffer = new();
    private int depth, stringStart, recordStart = -1;
    private bool inString, escaped;
    private string property = "", array = "";
    public void Append(string chunk)
    {
        var start = buffer.Length; buffer.Append(chunk);
        for (var i = start; i < buffer.Length; i++)
        {
            var c = buffer[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c != '"') continue;
                inString = false;
                if (depth == 1) property = JsonSerializer.Deserialize<string>(buffer.ToString(stringStart, i - stringStart + 1))!;
                continue;
            }
            if (c == '"') { inString = true; stringStart = i; }
            else if (c is '{' or '[')
            {
                if (c == '[' && depth == 1) array = property;
                if (c == '{' && depth == 2) recordStart = i;
                depth++;
            }
            else if (c is '}' or ']')
            {
                depth--;
                if (c == '}' && depth == 2 && recordStart >= 0)
                {
                    using var document = JsonDocument.Parse(buffer.ToString(recordStart, i - recordStart + 1));
                    recordReady(array, document.RootElement); recordStart = -1;
                }
            }
        }
    }
}
