using System.Text.RegularExpressions;

namespace BlueJays.Core.Translation;

/// <summary>Rejects large rewrites and obvious factual changes; this is not acoustic verification.</summary>
public static class CorrectionGuard
{
    public static bool IsConservative(string raw, string corrected)
    {
        if (raw == corrected) return true;
        if (string.IsNullOrWhiteSpace(corrected) || corrected.Length > 2000 ||
            corrected.Length < raw.Length * .6 || corrected.Length > raw.Length * 1.4 + 4) return false;
        const string numbers = @"[0-9０-９]+(?:[.:/：．／-][0-9０-９]+)*|[〇零一二三四五六七八九十百千万億兆]+(?=[年月日時分秒個人件円%％])";
        const string negation = @"ない|なく|なかった|ません|ぬ|\b(?:not|no|never|cannot|can't|don't|doesn't|didn't|won't|isn't|aren't)\b";
        const string decisions = @"承認|否認|却下|否決|可決|不要|必要|不可能|可能|中止|延期|\b(?:approv\w*|reject\w*|accept\w*|declin\w*|cancel\w*|postpon\w*)\b";
        static string[] Tokens(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.IgnoreCase).Select(m => m.Value.ToLowerInvariant()).ToArray();
        if (!Tokens(raw, numbers).SequenceEqual(Tokens(corrected, numbers)) ||
            !Tokens(raw, negation).SequenceEqual(Tokens(corrected, negation)) ||
            !Tokens(raw, decisions).SequenceEqual(Tokens(corrected, decisions))) return false;
        var limit = Math.Min(32, Math.Max(3, raw.Length / 4));
        if (Math.Abs(raw.Length - corrected.Length) > limit) return false;
        var row = Enumerable.Range(0, corrected.Length + 1).ToArray();
        for (var i = 1; i <= raw.Length; i++)
        {
            var diagonal = row[0]; row[0] = i;
            for (var j = 1; j <= corrected.Length; j++)
            {
                var old = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (raw[i - 1] == corrected[j - 1] ? 0 : 1));
                diagonal = old;
            }
        }
        return row[^1] <= limit;
    }
}
