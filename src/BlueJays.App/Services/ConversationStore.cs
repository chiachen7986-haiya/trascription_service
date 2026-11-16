using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BlueJays.App.ViewModels;
using BlueJays.Core.Models;

namespace BlueJays.App.Services;

public sealed record SavedTranslation(string Language, string Text, string State);
public sealed record SavedCaption(Transcript Transcript, SavedTranslation[] Translations);
public sealed record SavedConversation(string Id, ConversationOptions Options, SavedCaption[] Captions, string PriorityLanguage, bool ApiSucceeded);
public sealed record ConversationArchive(int Version, string? SelectedId, SavedConversation[] Conversations);

/// <summary>Atomic, Windows-user-encrypted snapshots. Audio and API credentials are never stored here.</summary>
public sealed class ConversationStore(string? directory = null)
{
    private readonly string path = Path.Combine(directory ?? SettingsStore.DataDirectory, "conversations.v1.dat");
    public (ConversationArchive? Archive, string? Warning) Load()
    {
        if (!File.Exists(path)) return (null, null);
        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            var archive = JsonSerializer.Deserialize<ConversationArchive>(json) ?? throw new JsonException();
            if (archive.Version != 1 || archive.Conversations is null || archive.Conversations.Any(c => c is null ||
                c.Options is null || c.Options.SpokenLanguages is null || c.Options.TargetLanguages is null || c.Captions is null ||
                string.IsNullOrWhiteSpace(c.Id) || c.Captions.Any(r => r is null || r.Transcript is null || r.Translations is null ||
                    string.IsNullOrWhiteSpace(r.Transcript.Id) || r.Transcript.Text is null || r.Transcript.RawText is null ||
                    r.Translations.Any(t => t is null || t.Language is null || t.Text is null || t.State is null)))) throw new JsonException();
            return (archive, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or UnauthorizedAccessException)
        { return (null, "Saved conversations could not be loaded. The original history file has been kept."); }
    }
    public void Save(ConversationArchive archive)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(archive), null, DataProtectionScope.CurrentUser);
        var temporary = path + ".tmp";
        try { File.WriteAllBytes(temporary, data); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static ConversationArchive Snapshot(MainViewModel model) => new(1, model.SelectedConversation?.Id,
        model.Conversations.Select(c => new SavedConversation(c.Id, c.Options,
            c.Captions.Select(r => new SavedCaption(r.Transcript, r.Translations.Select(t => new SavedTranslation(t.Language, t.Text, t.State)).ToArray())).ToArray(),
            c.PriorityLanguage, c.ApiSucceeded)).ToArray());
    public static void Restore(MainViewModel model, ConversationArchive archive)
    {
        model.Conversations.Clear();
        foreach (var saved in archive.Conversations)
        {
            var conversation = new ConversationViewModel(saved.Options) { Id = saved.Id,
                PriorityLanguage = saved.Options.TargetLanguages.Contains(saved.PriorityLanguage) ? saved.PriorityLanguage : "", ApiSucceeded = saved.ApiSucceeded };
            foreach (var caption in saved.Captions.OrderBy(c => c.Transcript.CapturedAt))
            {
                var row = new CaptionRow(caption.Transcript, saved.Options.TargetLanguages) { Restored = true };
                conversation.Memory.Record(caption.Transcript);
                foreach (var translation in caption.Translations)
                {
                    var state = translation.State is "Translating…" or "Updating translation…" or "Waiting for a complete clause…" ? "Stopped · unfinished" : translation.State;
                    row.SetTranslation(row.Revision, translation.Language, translation.Text, state);
                    if (state == "Translated") conversation.Memory.SetTranslation(row.Id, row.Revision, translation.Language, translation.Text);
                }
                conversation.Captions.Add(row);
                if (row.IsFullyTranslated) conversation.RoomCaption = row.Snapshot();
            }
            model.Conversations.Add(conversation);
        }
        model.SelectedConversation = model.Conversations.FirstOrDefault(c => c.Id == archive.SelectedId) ?? model.Conversations.LastOrDefault();
    }
}
