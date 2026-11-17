using System.ComponentModel;
using System.IO;
using System.Text;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BlueJays.App.Services;
using BlueJays.App.ViewModels;
using BlueJays.App.Views;
using BlueJays.Core.Models;
using BlueJays.Core.Translation;
using Microsoft.Win32;
using L = BlueJays.App.Localization.Localizer;

namespace BlueJays.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();
    private readonly SettingsStore store;
    private readonly ConversationStore history;
    private readonly TerminologyStore terminology;
    private readonly TokenUsageStore tokenUsage;
    private readonly System.Windows.Threading.DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SemaphoreSlim historyGate = new(1);
    private bool historyCloseReady, historyWritable = true;
    private long historyRevision, savedRevision;
    private AppSettings settings;
    private string apiKey;
    private InterpretationSession? session;
    private CaptionWindow? floatingWindow;
    private RoomWindow? roomWindow;
    private bool closeRequested;
    private bool followCaptions = true;
    private CancellationTokenSource? exportCancellation;

    public MainWindow() : this(new SettingsStore(), new ConversationStore()) { }

    public MainWindow(SettingsStore settingsStore, ConversationStore conversationStore)
    {
        store = settingsStore; history = conversationStore;
        terminology = new(store.UserDataDirectory);
        tokenUsage = new(store.UserDataDirectory);
        var loaded = store.Load();
        settings = loaded.Settings;
        // Migrate the MVP's overly short 4 s / 450 ms defaults to conversation-friendly boundaries.
        settings = settings with
        {
            SilenceMilliseconds = settings.SilenceMilliseconds < 700 ? 1000 : settings.SilenceMilliseconds,
            MaxPhraseSeconds = settings.MaxPhraseSeconds < 8 ? 8 : settings.MaxPhraseSeconds
        };
        apiKey = loaded.ApiKey;
        L.Current.SetLanguage(settings.UiLanguage);
        InitializeComponent();
        WindowSizing.FitToWorkArea(this);
        DataContext = viewModel;
        var saved = history.Load();
        if (saved.Archive is not null) ConversationStore.Restore(viewModel, saved.Archive);
        historyWritable = saved.Warning is null;
        viewModel.HistoryChanged += ScheduleHistorySave;
        saveTimer.Tick += async (_, _) => { saveTimer.Stop(); await SaveHistoryAsync(); };
        foreach (var source in settings.Sources) viewModel.Sources.Add(new(source));
        SourceAdder.IsExpanded = viewModel.Sources.Count == 0;
        viewModel.Notice = saved.Warning ?? loaded.Warning ?? L.T("Conversations are saved locally with Windows user encryption. Translation sends text to your selected API.");
        Loaded += (_, _) => RefreshDevices();
        Closing += OnClosing;
    }

    private void NewConversation_Click(object sender, RoutedEventArgs e)
    {
        TerminologyDictionary[] dictionaries;
        try { dictionaries = terminology.Load(); }
        catch (Exception ex) when (TerminologyStore.IsFileError(ex))
        {
            dictionaries = [];
            viewModel.Notice = L.T("Could not load dictionaries.");
        }
        var dialog = new ConversationWindow(settings, dictionaries) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        var conversation = new ConversationViewModel(dialog.Result);
        viewModel.Conversations.Add(conversation); viewModel.SelectedConversation = conversation;
        viewModel.Status = "Conversation ready · add a source, then start";
        viewModel.Notice = L.T("Conversations are saved locally with Windows user encryption. Translation sends text to your selected API.");
        ScheduleHistorySave();
    }

    private void ScheduleHistorySave() { saveTimer.Stop(); saveTimer.Start(); }
    private async Task<bool> SaveHistoryAsync()
    {
        if (!historyWritable)
        { viewModel.Notice = L.T("Saved history could not be read. Export new conversations or use Clear all to reset history."); return false; }
        var snapshot = ConversationStore.Snapshot(viewModel); var revision = ++historyRevision;
        await historyGate.WaitAsync();
        try
        {
            if (revision > savedRevision) { await Task.Run(() => history.Save(snapshot)); savedRevision = revision; }
            return true;
        }
        catch (Exception ex) { viewModel.Notice = L.F("Could not save conversation history: {0}", ex.Message); return false; }
        finally { historyGate.Release(); }
    }
    private async void DeleteConversation_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanConfigure || viewModel.SelectedConversation is not { } selected) return;
        if (MessageBox.Show(this, L.T("Delete the selected conversation and its saved transcripts?"), L.T("Delete conversation"), MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        viewModel.Conversations.Remove(selected); viewModel.SelectedConversation = viewModel.Conversations.LastOrDefault();
        saveTimer.Stop(); await SaveHistoryAsync();
    }
    private async void ClearConversations_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanConfigure) return;
        if (MessageBox.Show(this, L.T("Permanently delete all saved conversations and transcripts?"), L.T("Clear all conversations"), MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        viewModel.Conversations.Clear(); viewModel.SelectedConversation = null; historyWritable = true;
        saveTimer.Stop(); await SaveHistoryAsync();
    }
    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void RefreshDevices()
    {
        if (DeviceBox is null) return; // SelectionChanged can fire while XAML is being loaded.
        try
        {
            DeviceBox.ItemsSource = AudioDeviceService.List(SourceKindBox.SelectedIndex == 0 ? AudioSourceKind.Microphone : AudioSourceKind.ComputerAudio);
            DeviceBox.SelectedIndex = 0;
            if (!viewModel.Recording && !viewModel.Busy)
                foreach (var row in viewModel.Sources) row.SetStatus(AudioDeviceService.IsAvailable(row.Settings) ? "Ready" : "Unavailable · reconnect or choose device");
        }
        catch (Exception ex) { viewModel.Notice = L.F("Could not list audio devices: {0}", ex.Message); }
    }

    private void SourceKind_Changed(object sender, SelectionChangedEventArgs e) => RefreshDevices();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void AddSource_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not AudioDevice device) { viewModel.Notice = "No active device found. Connect a microphone or audio output and press Refresh."; return; }
        if (viewModel.Sources.Any(s => s.Settings.DeviceId == device.Id)) { viewModel.Notice = "This device is already in the source list."; return; }
        if (viewModel.Sources.Count >= 4) { viewModel.Notice = "The MVP supports up to four sources. Fewer active sources give faster captions."; return; }
        var count = viewModel.Sources.Count(s => s.Settings.Kind == device.Kind) + 1;
        viewModel.Sources.Add(new(new() { DeviceId = device.Id, Kind = device.Kind, DeviceName = device.Name,
            Name = $"{L.T(device.Kind == AudioSourceKind.Microphone ? "Microphone" : "Computer audio")} {count}" }));
        SourceAdder.IsExpanded = false;
        SaveSettings();
    }

    private void SourceName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: SourceRow row } || !viewModel.CanConfigure) return;
        row.Name = string.IsNullOrWhiteSpace(row.Name) ? row.DeviceName : row.Name.Trim();
        viewModel.RenameSource(row.Id, row.Name);
        SaveSettings();
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SourceRow row }) viewModel.Sources.Remove(row);
        SaveSettings();
    }

    private void ChooseDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SourceRow row } || !viewModel.CanConfigure) return;
        var dialog = new AudioSourceDeviceWindow(row.Settings) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } device) return;
        if (viewModel.Sources.Any(s => s.Id != row.Id && s.Settings.DeviceId == device.Id))
        { viewModel.Notice = "This device is already in the source list."; return; }
        row.ChangeDevice(device.Id, device.Name); SaveSettings(); RefreshDevices();
    }

    private void SaveSettings()
    {
        settings = settings with { Sources = viewModel.Sources.Select(s => s.Settings).ToList() };
        try { store.Save(settings, apiKey); }
        catch (Exception ex) { viewModel.Notice = L.F("Settings could not be saved: {0}", ex.Message); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void ShowSettings()
    {
        var dialog = new SettingsWindow(settings, apiKey, terminology, tokenUsage) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        settings = dialog.Result;
        apiKey = dialog.ApiKey;
        L.Current.SetLanguage(settings.UiLanguage);
        SaveSettings();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanStart) return;
        var conversation = viewModel.SelectedConversation!;
        if (!SpeechModels.IsReady(settings)) ShowSettings();
        if (!SpeechModels.IsReady(settings)) { viewModel.Notice = L.T("Select or download the speech model in Settings first."); return; }
        if (!viewModel.Sources.Any(s => s.Enabled)) { viewModel.Notice = "Add and enable at least one audio source."; return; }
        try
        {
            if (conversation.Options.TargetLanguages.Count > 0) _ = TranslationClient.GetEndpoint(settings.ApiBaseUrl);
            SaveSettings();
            viewModel.Busy = true;
            viewModel.Status = "Loading local speech model…";
            viewModel.Notice = "";
            var options = conversation.Options;
            if (options.TerminologyDictionaryId is { } dictionaryId)
                options = terminology.Load().FirstOrDefault(dictionary => dictionary.Id == dictionaryId)?.ApplyTo(options) ?? options;
            var active = new InterpretationSession(settings, apiKey, options, conversation.Memory, recordUsage: tokenUsage.Record);
            session = active;
            // Workers never touch WPF objects directly. Ignore late updates from a stopped session.
            active.Recognized += caption => Dispatch(active, () => viewModel.Add(caption));
            active.TranslationChanged += (id, revision, language, text, status) => Dispatch(active, () =>
            {
                viewModel.ApplyTranslation(id, revision, language, text, status);
            });
            active.LevelChanged += (id, level) => Dispatch(active, () =>
            {
                var source = viewModel.Sources.FirstOrDefault(s => s.Id == id);
                if (source is not null) source.Level = level;
            });
            active.Warning += message => Dispatch(active, () => viewModel.Notice = message);
            active.SourceStateChanged += (id, state) => Dispatch(active, () => viewModel.Sources.FirstOrDefault(s => s.Id == id)?.SetStatus(state));
            active.SpeechLagChanged += seconds => Dispatch(active, () => { if (!viewModel.Busy) ShowListeningStatus(active, seconds); });
            active.Failed += message => Dispatcher.BeginInvoke(async () =>
            {
                if (session != active) return;
                viewModel.Notice = message;
                if (!viewModel.Busy) await StopSessionAsync(finish: false);
            });
            await active.StartAsync();
            viewModel.Recording = true;
            ShowListeningStatus(active);
        }
        catch (Exception ex)
        {
            viewModel.Notice = L.F("Could not start: {0}", ex.Message);
            await StopSessionAsync(finish: false);
        }
        finally { viewModel.Busy = false; if (closeRequested) Close(); }
    }

    private void Dispatch(InterpretationSession active, Action action) => Dispatcher.BeginInvoke(() =>
    {
        if (session == active) action();
    });

    private void ShowListeningStatus(InterpretationSession active, double? lag = null)
    {
        // Put live latency first so it remains visible in a narrow window.
        viewModel.Status = active.BackendName + " · " + (lag is null ? "" : L.F("STT delay: {0:F1}s", lag) + " · ") +
            L.F("Listening · {0}/{1} audio sources", active.ActiveSourceCount, settings.Sources.Count(s => s.Enabled));
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopSessionAsync();

    private async Task StopSessionAsync(bool finish = true)
    {
        viewModel.Busy = true;
        viewModel.Status = "Finishing the last utterance and translations…";
        var previous = session;
        try
        {
            if (previous is not null)
            {
                if (finish) await previous.FinishAsync();
                // Drain already-posted WPF events before disconnecting this session.
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                session = null;
                await previous.DisposeAsync();
            }
        }
        catch (Exception ex) { viewModel.Notice = L.F("Stopped with an error: {0}", ex.Message); }
        finally
        {
            foreach (var source in viewModel.Sources)
            { source.Level = 0; if (source.State == "Listening") source.SetStatus("Ready"); }
            session = null;
            foreach (var row in viewModel.Captions) foreach (var translation in row.Translations)
                if (translation.State is "Translating…" or "Updating translation…" or "Waiting for a complete clause…") translation.Update("", "Stopped · unfinished");
            viewModel.Recording = false;
            viewModel.Busy = false;
            viewModel.Status = "Stopped · ready to listen again";
            ScheduleHistorySave();
        }
    }

    private void Float_Click(object sender, RoutedEventArgs e)
    {
        if (floatingWindow is null)
        {
            floatingWindow = new CaptionWindow { DataContext = viewModel };
            floatingWindow.Closed += (_, _) => floatingWindow = null;
            floatingWindow.Show();
        }
        else floatingWindow.Activate();
    }

    private void Room_Click(object sender, RoutedEventArgs e)
    {
        if (roomWindow is null)
        {
            roomWindow = new RoomWindow { DataContext = viewModel };
            roomWindow.Closed += (_, _) => roomWindow = null;
            roomWindow.Show();
        }
        else roomWindow.Activate();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.Exporting) return;
        if (viewModel.Captions.Count == 0) { viewModel.Notice = "No captions to export yet."; return; }
        var dialog = new SaveFileDialog { Filter = L.T("Text file (*.txt)|*.txt"), FileName = $"Transcription-Service-{DateTime.Now:yyyyMMdd-HHmm}.txt" };
        if (dialog.ShowDialog(this) != true) return;
        var snapshot = new ExportSnapshot(viewModel.ConversationTitle, viewModel.LanguageSummary, L.Current.Language,
            viewModel.Captions.Select(c => new ExportCaption(new(c.CapturedAt, c.Source, c.Original, c.IsFinal),
                c.Translations.Select(t => new ExportTranslation(t.Language, t.Text, t.State)).ToArray(), c.RawText)).ToArray());
        var succeeded = viewModel.SelectedConversation?.ApiSucceeded == true;
        exportCancellation = new(); viewModel.Exporting = true;
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var progress = new Progress<string>(value => viewModel.Notice = L.F("Generating meeting summary… {0}", value));
            var result = await new TranscriptExporter(http, tokenUsage.Record).ExportAsync(dialog.FileName, snapshot, settings, apiKey, succeeded, progress, exportCancellation.Token);
            viewModel.Notice = result.SummaryError is null ? L.T("Captions exported.") : L.F("Exported transcript; summary unavailable: {0}", result.SummaryError);
        }
        catch (Exception ex) { viewModel.Notice = L.F("Export failed: {0}", ex.Message); }
        finally
        {
            exportCancellation.Dispose(); exportCancellation = null; viewModel.Exporting = false;
            if (closeRequested && !viewModel.Busy) Close();
        }
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e) => exportCancellation?.Cancel();

    private void CaptionScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0) followCaptions = CaptionScroll.VerticalOffset >= CaptionScroll.ScrollableHeight - 30;
        if (followCaptions && e.ExtentHeightChange > 0) CaptionScroll.ScrollToEnd();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        exportCancellation?.Cancel();
        if (viewModel.Exporting) { closeRequested = true; e.Cancel = true; return; }
        if (viewModel.Busy) { closeRequested = true; e.Cancel = true; return; }
        if (session is not null)
        {
            closeRequested = true;
            e.Cancel = true;
            await StopSessionAsync();
            Close();
            return;
        }
        if (!historyCloseReady)
        {
            e.Cancel = true; saveTimer.Stop();
            // A corrupt original archive is preserved; normal write failures leave
            // the window open so the user can retry or export their conversation.
            if ((!historyWritable && viewModel.Conversations.Count > 0) || (historyWritable && !await SaveHistoryAsync()))
            {
                if (MessageBox.Show(this, L.T("Conversation history could not be saved. Close without saving? Choose No to export your transcripts first."),
                    "Transcription Service", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                { closeRequested = false; return; }
            }
            historyCloseReady = true; await Task.Yield(); Close(); return;
        }
        SaveSettings();
        floatingWindow?.Close();
        roomWindow?.Close();
    }
}
