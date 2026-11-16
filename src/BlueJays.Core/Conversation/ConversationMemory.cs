using System.Text.Json;
using BlueJays.Core.Models;
using BlueJays.Core.Translation;

namespace BlueJays.Core.Conversation;

/// <summary>Conversation-local context shared by all audio sources and retained across Stop/Resume.</summary>
public sealed class ConversationMemory
{
    private sealed record Entry(Transcript Caption, Dictionary<string, string> Translations);
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new();

    public void Record(Transcript caption)
    {
        lock (gate)
        {
            if (entries.TryGetValue(caption.Id, out var old) && old.Caption.Revision > caption.Revision) return;
            var sameText = old is not null && old.Caption.Text == caption.Text;
            entries[caption.Id] = new(caption, sameText ? old!.Translations : new());
            if (entries.Count > 500) entries.Remove(entries.Values.MinBy(e => e.Caption.CapturedAt)!.Caption.Id);
        }
    }

    public bool IsCurrent(string id, int revision)
    { lock (gate) return entries.TryGetValue(id, out var entry) && entry.Caption.Revision == revision; }

    public bool TryResolve(TranslationItem item, out Transcript caption)
    {
        lock (gate)
        {
            if (entries.TryGetValue(item.Id, out var entry) &&
                (entry.Caption.RawText.Length > 0 ? entry.Caption.RawText : entry.Caption.Text) == item.Text)
            { caption = entry.Caption; return true; }
            caption = null!; return false;
        }
    }

    public Transcript? ApplyCorrection(TranslationItem item, string text)
    {
        lock (gate)
        {
            if (!TryResolve(item, out var caption) || text == caption.Text) return null;
            var corrected = caption with { Text = text, RawText = item.Text, Revision = caption.Revision + 1 };
            entries[item.Id] = new(corrected, new()); return corrected;
        }
    }
    public void RenameSource(string sourceId, string name)
    {
        lock (gate)
            foreach (var entry in entries.Values.Where(e => e.Caption.SourceId == sourceId).ToList())
                entries[entry.Caption.Id] = entry with { Caption = entry.Caption with { SourceName = name } };
    }

    public void SetTranslation(string id, int revision, string language, string text)
    {
        lock (gate) if (entries.TryGetValue(id, out var entry) && entry.Caption.Revision == revision)
            entry.Translations[language] = text;
    }

    public string SourcePrompt(string sourceId)
    {
        lock (gate)
        {
            var text = string.Join(" ", entries.Values.Where(e => e.Caption.SourceId == sourceId && e.Caption.IsFinal)
                .OrderBy(e => e.Caption.CapturedAt).TakeLast(3).Select(e => e.Caption.RawText.Length > 0 ? e.Caption.RawText : e.Caption.Text));
            return text.Length > 700 ? text[^700..] : text;
        }
    }

    public string Context(IReadOnlySet<string> exclude)
    {
        lock (gate)
        {
            var selected = new List<object>(); var bytes = 0;
            foreach (var entry in entries.Values.Where(e => e.Caption.IsFinal && !exclude.Contains(e.Caption.Id)).OrderByDescending(e => e.Caption.CapturedAt))
            {
                var item = new { source = entry.Caption.SourceName, time = entry.Caption.CapturedAt, text = entry.Caption.Text, translations = entry.Translations };
                var size = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(item));
                if (bytes + size > 8000 || selected.Count == 24) break;
                selected.Add(item); bytes += size;
            }
            selected.Reverse(); return PromptJson.Serialize(selected);
        }
    }
}
