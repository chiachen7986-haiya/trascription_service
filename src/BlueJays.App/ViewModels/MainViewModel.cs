using System.Collections.ObjectModel;
using BlueJays.Core.Models;
using BlueJays.Core.Conversation;
using BlueJays.App.Localization;

namespace BlueJays.App.ViewModels;

public sealed class SourceRow(AudioSourceSettings settings) : ObservableObject
{
    private bool enabled = settings.Enabled;
    private string name = settings.Name;
    private double level;
    private string status = "Ready";
    public AudioSourceSettings Settings => settings with { Enabled = Enabled, Name = Name.Trim(), DeviceName = DeviceName };
    public string Id => settings.Id;
    public string Name { get => name; set => Set(ref name, value); }
    public string DeviceName => settings.DeviceName.Length > 0 ? settings.DeviceName : settings.Name;
    public string KindLabel => Localizer.T(settings.Kind == AudioSourceKind.Microphone ? "Microphone" : "Computer audio");
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public double Level { get => level; set => Set(ref level, value * 100); }
    public string State => status;
    public string Status => Localizer.T(status);
    public string StatusColor => status == "Unavailable · reconnect or choose device" ? "#946119" : status == "Listening" ? "#188567" : "#65748B";
    public void SetStatus(string value) { Set(ref status, value, nameof(Status)); Raise(nameof(State)); Raise(nameof(StatusColor)); }
    public void ChangeDevice(string id, string deviceName)
    {
        // Preserve the source identity/custom label and its existing conversation history.
        settings = settings with { DeviceId = id, DeviceName = deviceName };
        Raise(nameof(DeviceName)); Raise(nameof(Settings)); SetStatus("Ready");
    }
}

public sealed class TranslationRow(string language, int index) : ObservableObject
{
    private string text = "", status = "Waiting for a complete clause…";
    public string Language => language;
    public string DisplayLanguage => Localizer.T(language);
    public string Color => new[] { "#FFF0AA", "#D7EAB0", "#CCE4FA", "#E7D9F7" }[index % 4];
    public string Text { get => text; set => Set(ref text, value); }
    public string State => status;
    public string Status => Localizer.T(status);
    public string DisplayText => Text.Length == 0 ? Status : Text;
    public void Update(string value, string state)
    {
        if (value.Length > 0) Text = value;
        Set(ref status, state, nameof(Status)); Raise(nameof(State)); Raise(nameof(DisplayText));
    }
    public void Invalidate() { Text = ""; Update("", "Updating translation…"); }
}

public sealed class CaptionRow : ObservableObject
{
    private Transcript transcript;
    public Transcript Transcript => transcript;
    public bool Restored { get; set; }
    public CaptionRow(Transcript transcript, IReadOnlyList<string> targets)
    {
        this.transcript = transcript;
        for (var i = 0; i < targets.Count; i++) Translations.Add(new(targets[i], i));
    }
    public string Id => transcript.Id;
    public int Revision => transcript.Revision;
    public bool IsFinal => transcript.IsFinal;
    public string Source => transcript.SourceName;
    public string Time => transcript.CapturedAt.ToString("HH:mm:ss");
    public DateTimeOffset CapturedAt => transcript.CapturedAt;
    public string Original => transcript.Text;
    public string RawText => transcript.RawText.Length > 0 ? transcript.RawText : transcript.Text;
    public bool HasCorrection => transcript.RawText.Length > 0 && transcript.RawText != transcript.Text;
    public string Status => HasCorrection ? Localizer.T("Context adjusted · raw STT available") : IsFinal ? Localizer.T("Confirmed") : Restored ? Localizer.T("Saved · unfinished") : Localizer.F("Listening · draft · {0} characters stable", transcript.StableLength);
    public bool IsFullyTranslated => IsFinal && Translations.Count > 0 && Translations.All(t => t.State == "Translated" && t.Text.Length > 0);
    public ObservableCollection<TranslationRow> Translations { get; } = [];
    public void Update(Transcript value)
    {
        if (value.Revision < Revision || IsFinal && !value.IsFinal) return;
        var unchanged = transcript.Text == value.Text;
        transcript = value;
        Restored = false;
        Raise(nameof(Original)); Raise(nameof(Revision)); Raise(nameof(IsFinal)); Raise(nameof(Status));
        Raise(nameof(RawText)); Raise(nameof(HasCorrection));
        if (!unchanged) foreach (var row in Translations) row.Invalidate();
    }
    public void SetTranslation(int revision, string language, string text, string status)
    { if (revision == Revision) Translations.FirstOrDefault(t => t.Language == language)?.Update(text, status); }
    public CaptionRow Snapshot()
    {
        var snapshot = new CaptionRow(transcript, Translations.Select(t => t.Language).ToList());
        foreach (var row in Translations) snapshot.SetTranslation(Revision, row.Language, row.Text, row.State);
        return snapshot;
    }
    public void RenameSource(string sourceId, string name)
    {
        if (transcript.SourceId != sourceId) return;
        transcript = transcript with { SourceName = name }; Raise(nameof(Source));
    }
}

public sealed class ConversationViewModel(ConversationOptions options)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string PriorityLanguage { get; set; } = options.TargetLanguages.FirstOrDefault() ?? "";
    public IReadOnlyList<TranslationRow> Placeholders { get; } = options.TargetLanguages.Select((language, index) => new TranslationRow(language, index)).ToList();
    public ConversationOptions Options => options;
    public string Name => options.Name;
    public string Languages => string.Join(" / ", options.TargetLanguages);
    public ConversationMemory Memory { get; } = new();
    public ObservableCollection<CaptionRow> Captions { get; } = [];
    // A detached display snapshot prevents new drafts from blanking the room's completed card.
    public CaptionRow? RoomCaption { get; set; }
    public bool ApiSucceeded { get; set; }
}

public sealed class MainViewModel : ObservableObject
{
    public event Action? HistoryChanged;
    public sealed record FloatingLanguage(string Code, string Name, bool IsSelected);
    public sealed record FloatingLine(string Text, bool IsPriority);
    public IEnumerable<FloatingLanguage> FloatingLanguages => new[] { new FloatingLanguage("", Localizer.T("Original"), PriorityLanguage.Length == 0) }
        .Concat((selected?.Options.TargetLanguages ?? []).Select(l => new FloatingLanguage(l, Localizer.T(l), l == PriorityLanguage)));
    public string PriorityLanguage
    {
        get => selected?.PriorityLanguage ?? "";
        set { if (selected is null) return; selected.PriorityLanguage = value; Changed(); HistoryChanged?.Invoke(); }
    }
    public IEnumerable<FloatingLine> FloatingLines => RecentCaptions.SelectMany(c =>
        new[] { new FloatingLine(c.Source + ": " + c.Original, PriorityLanguage.Length == 0) }.Concat(
            c.Translations.Where(t => t.Text.Length > 0).OrderByDescending(t => t.Language == PriorityLanguage)
                .Select(t => new FloatingLine(c.Source + " · " + t.DisplayLanguage + ": " + t.Text, t.Language == PriorityLanguage))));
    private string status = "Create a conversation to begin", notice = "", modelSummary = "", sessionSummary = "";
    private bool busy, recording;
    private bool exporting;
    private ConversationViewModel? selected;
    private readonly ObservableCollection<CaptionRow> empty = [];
    public ObservableCollection<SourceRow> Sources { get; } = [];
    public ObservableCollection<ConversationViewModel> Conversations { get; } = [];
    public ConversationViewModel? SelectedConversation
    {
        get => selected;
        set { if (!Set(ref selected, value)) return; Raise(nameof(Captions)); Changed(); RefreshControls(); HistoryChanged?.Invoke(); }
    }
    public ObservableCollection<CaptionRow> Captions => selected?.Captions ?? empty;
    public IEnumerable<CaptionRow> RecentCaptions => Captions.TakeLast(3);
    public CaptionRow? LatestCaption => Captions.LastOrDefault();
    public CaptionRow? RoomCaption => selected?.RoomCaption ?? LatestCaption;
    public string RoomSource => RoomCaption?.Source ?? Localizer.T("Waiting for speech");
    public string RoomOriginal => RoomCaption?.Original ?? Localizer.T("The original speech will appear here.");
    public IEnumerable<TranslationRow> DisplayTranslations => RoomCaption?.Translations.AsEnumerable() ?? selected?.Placeholders ?? [];
    public string RoomStatus => Localizer.T(Recording ? "Listening · the last translation stays visible until the next is ready." : "Ready · completed translations remain visible.");
    public int RoomColumns => (selected?.Options.TargetLanguages.Count ?? 0) <= 1 ? 1 : 2;
    public bool HasNoTargets => selected is null || selected.Options.TargetLanguages.Count == 0;
    public string ConversationTitle => selected?.Name ?? Localizer.T("No conversation selected");
    public string LanguageSummary => selected is null ? Localizer.T("Choose languages with New Conversation") :
        Localizer.F("Spoken: {0} · Output: {1}", string.Join(", ", selected.Options.SpokenLanguages), selected.Languages.Length > 0 ? string.Join(" / ", selected.Options.TargetLanguages.Select(Localizer.T)) : Localizer.T("Transcription only"));
    public string Status { get => Localizer.T(status); set => Set(ref status, value); }
    public string Notice { get => Localizer.T(notice); set => Set(ref notice, value); }
    public string ModelSummary { get => modelSummary; set => Set(ref modelSummary, value); }
    public string SessionSummary { get => sessionSummary; set => Set(ref sessionSummary, value); }
    public bool Busy { get => busy; set { Set(ref busy, value); RefreshControls(); } }
    public bool Recording { get => recording; set { Set(ref recording, value); RefreshControls(); Raise(nameof(RoomStatus)); } }
    public bool Exporting { get => exporting; set { Set(ref exporting, value); Raise(nameof(CanExport)); RefreshControls(); } }
    public bool CanExport => !Exporting && Captions.Count > 0;
    public bool CanConfigure => !Busy && !Recording && !Exporting;
    public bool CanStart => CanConfigure && selected is not null;
    public bool CanStop => Recording && !Busy;
    public bool IsEmpty => Captions.Count == 0;
    private void RefreshControls() { Raise(nameof(CanConfigure)); Raise(nameof(CanStart)); Raise(nameof(CanStop)); }
    private void Changed()
    {
        Raise(nameof(FloatingLanguages)); Raise(nameof(FloatingLines)); Raise(nameof(PriorityLanguage));
        Raise(nameof(RecentCaptions)); Raise(nameof(LatestCaption)); Raise(nameof(IsEmpty));
        Raise(nameof(ConversationTitle)); Raise(nameof(LanguageSummary));
        Raise(nameof(DisplayTranslations)); Raise(nameof(RoomColumns));
        Raise(nameof(HasNoTargets));
        Raise(nameof(RoomCaption)); Raise(nameof(RoomSource)); Raise(nameof(RoomOriginal)); Raise(nameof(CanExport));
    }
    public void Add(Transcript caption)
    {
        if (selected is null) return;
        var row = Captions.FirstOrDefault(c => c.Id == caption.Id);
        if (row is not null) row.Update(caption);
        else
        {
            var insert = 0;
            while (insert < Captions.Count && Captions[insert].CapturedAt <= caption.CapturedAt) insert++;
            Captions.Insert(insert, new(caption, selected.Options.TargetLanguages));
        }
        Changed();
        HistoryChanged?.Invoke();
    }
    public void Clear() { Captions.Clear(); Changed(); }
    public void ApplyTranslation(string id, int revision, string language, string text, string status)
    {
        var caption = Captions.FirstOrDefault(c => c.Id == id);
        caption?.SetTranslation(revision, language, text, status);
        Raise(nameof(FloatingLines)); HistoryChanged?.Invoke();
        if (caption?.IsFullyTranslated == true && selected is not null)
        {
            selected.ApiSucceeded = true;
            if (selected.RoomCaption is null || caption.CapturedAt >= selected.RoomCaption.CapturedAt)
                selected.RoomCaption = caption.Snapshot();
            Changed();
        }
    }
    public void RenameSource(string sourceId, string name)
    {
        foreach (var conversation in Conversations)
        {
            foreach (var caption in conversation.Captions) caption.RenameSource(sourceId, name);
            conversation.RoomCaption?.RenameSource(sourceId, name);
            conversation.Memory.RenameSource(sourceId, name);
        }
        Changed();
        HistoryChanged?.Invoke();
    }
}
