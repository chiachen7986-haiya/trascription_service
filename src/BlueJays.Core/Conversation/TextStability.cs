using System.Text;

namespace BlueJays.Core.Conversation;

public static class TextStability
{
    public static string CommonPrefix(string previous, string current)
    {
        var count = 0;
        while (count < Math.Min(previous.Length, current.Length) && previous[count] == current[count]) count++;
        // Do not confirm half of a Latin word. CJK does not use spaces as word delimiters.
        if (count < current.Length && count > 0 && current[count - 1] < 0x3000 && char.IsLetterOrDigit(current[count - 1]))
            while (count > 0 && char.IsLetterOrDigit(current[count - 1])) count--;
        return current[..count].TrimEnd();
    }

    public static string TranslationPrefix(string stable)
    {
        var end = stable.LastIndexOfAny(['.', '?', '!', '。', '？', '！', ';', '；', ',', '，', '、']);
        return end >= 0 ? stable[..(end + 1)].Trim() : "";
    }

    public static string RemoveOverlap(string previous, string current, int minimumCharacters = 4)
    {
        // Compare normalized characters while retaining an index into the original text.
        static (string Text, List<int> Ends) Normalize(string text)
        {
            var value = new StringBuilder(); var ends = new List<int>();
            for (var i = 0; i < text.Length; i++) if (char.IsLetterOrDigit(text[i])) { value.Append(char.ToLowerInvariant(text[i])); ends.Add(i + 1); }
            return (value.ToString(), ends);
        }
        var left = Normalize(previous).Text; var right = Normalize(current);
        for (var length = Math.Min(left.Length, right.Text.Length); length >= minimumCharacters; length--)
        {
            if (!left.EndsWith(right.Text[..length], StringComparison.Ordinal)) continue;
            var end = right.Ends[length - 1];
            if (end < current.Length && current[end - 1] < 0x3000 && char.IsLetterOrDigit(current[end])) continue;
            return current[end..].TrimStart(' ', ',', '.', '，', '。', '、');
        }
        return current;
    }
}
