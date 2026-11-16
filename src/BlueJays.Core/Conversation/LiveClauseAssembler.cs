using System.Text;
using BlueJays.Core.Models;
using BlueJays.Core.Translation;

namespace BlueJays.Core.Conversation;

public sealed record ClauseUpdate(IReadOnlyList<Transcript> Captions, long CommittedSample);

/// <summary>Freezes agreed clauses while leaving one tail editable, including during continuous speech.</summary>
public sealed class LiveClauseAssembler
{
    private string previous = "", committed = "";
    private int clause, revision;
    private long committedSample;
    public long CommittedSample => committedSample;
    private readonly Dictionary<int, DateTimeOffset> times = new();

    public ClauseUpdate Update(CapturedWindow capture, IReadOnlyList<RecognizedPart> parts, string previousUtterance = "")
    {
        var window = capture.Window;
        var remaining = parts.Where(p => window.StartSample + (long)(p.EndSeconds * 16000) > committedSample).ToList();
        // A mixed/unknown window must not bypass translation based on one guessed language.
        var languages = remaining.Where(p => !string.IsNullOrWhiteSpace(p.Text)).Select(p => TranslationLanguage.Normalize(p.Language)).Distinct().ToArray();
        var language = languages.Length == 1 ? languages[0] : "";
        var rawParts = string.Concat(remaining.Select(p => p.Text));
        var original = rawParts.Trim();
        var leadingWhitespace = rawParts.Length - rawParts.TrimStart().Length;
        var beginsAfterCommit = committedSample > 0 && remaining.Count > 0 &&
            window.StartSample + (long)(remaining[0].StartSeconds * 16000) >= committedSample;
        var text = beginsAfterCommit ? original : TextStability.RemoveOverlap(
            committed.Length > 0 ? committed : window.HasOverlap ? previousUtterance : "", original, committed.Length > 0 ? 1 : 4);
        if (text.Length == 0 && window.IsFinal) text = previous;
        if (text.Length == 0) return new([], committedSample);
        var stable = window.IsFinal ? text : TextStability.CommonPrefix(previous, text);
        var ready = window.IsFinal ? text : CompleteClause(stable);
        var result = new List<Transcript>();
        if (ready.Length > 0)
        {
            result.Add(Create(ready, true, ready.Length));
            // Only drop audio at a real Whisper segment boundary covered by the
            // confirmed text. Never estimate a cut from character count.
            var removedPrefix = original.Length - text.Length;
            var covered = removedPrefix + ready.Length;
            var position = -leadingWhitespace;
            foreach (var part in remaining)
            {
                position += part.Text.Length;
                var sample = window.StartSample + (long)(part.EndSeconds * 16000);
                if (position <= covered && sample <= window.StartSample + window.Samples.Length - 4800)
                    committedSample = Math.Max(committedSample, sample);
            }
            committed = (committed + " " + ready).Trim();
            if (committed.Length > 1500) committed = committed[^1500..];
            clause++; revision = 0;
            text = text[ready.Length..].Trim();
        }
        if (text.Length > 0) result.Add(Create(text, window.IsFinal, Math.Min(text.Length, Math.Max(0, stable.Length - ready.Length))));
        previous = text;
        return new(result, committedSample);

        Transcript Create(string value, bool final, int stableLength)
        {
            if (!times.TryGetValue(clause, out var time))
            {
                time = window.StartedAt.AddSeconds((window.StartSample + Math.Max(0, committedSample - window.StartSample)) / 16000d);
                times[clause] = time;
            }
            return new(window.Id + ":" + clause, capture.SourceName, time, value, capture.SourceId, ++revision, final, stableLength, Language: language);
        }
    }

    public static string CompleteClause(string stable)
    {
        // Commas alone are too weak for short Japanese fragments. Prefer sentence
        // endings, or a substantial clause, so subjects/negations stay together.
        for (var i = stable.Length - 1; i >= 0; i--)
        {
            var c = stable[i];
            var sentence = c is '.' or '?' or '!' or '。' or '？' or '！';
            if (c == '.' && i > 0 && i + 1 < stable.Length && char.IsDigit(stable[i - 1]) && char.IsDigit(stable[i + 1])) continue;
            if (sentence || i >= 40 && c is ';' or '；' or ',' or '，' or '、') return stable[..(i + 1)].Trim();
        }
        return "";
    }
}
