using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueJays.App;
using BlueJays.App.Services;
using BlueJays.App.ViewModels;
using BlueJays.App.Views;
using BlueJays.Core.Models;
using BlueJays.App.Localization;
using System.Windows.Controls;
using System.Text.RegularExpressions;

/// <summary>Render WPF content to artifacts. No windows are shown and no desktop input is sent.</summary>
internal static class UiSmoke
{
    public static Task Run(string folder)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                var trace = new BindingErrors();
                PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                // Load exactly the production styles without running the real
                // application startup, mutex, shell or user-data lifecycle.
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/TranscriptionService;component/Theme.xaml", UriKind.Absolute) });
                // Exercise the real window's debounced autosave and startup restore
                // against injected temporary stores, never the user's history.
                var historyDirectory = Path.Combine(Path.GetTempPath(), "Transcription-window-history-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var history = new BlueJays.App.Services.ConversationStore(historyDirectory);
                    var shell = new MainWindow(new BlueJays.App.Services.SettingsStore(historyDirectory), history);
                    var state = (MainViewModel)shell.DataContext;
                    var savedConversation = new ConversationViewModel(new() { Name = "Saved meeting", TargetLanguages = ["English"] });
                    state.Conversations.Add(savedConversation); state.SelectedConversation = savedConversation;
                    state.Add(new("saved", "Room", DateTimeOffset.Now, "保存した会話", "mic"));
                    var until = Stopwatch.StartNew();
                    while (history.Load().Archive is null && until.Elapsed.TotalSeconds < 5)
                    {
                        var frame = new DispatcherFrame();
                        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => { frame.Continue = false; }, Dispatcher.CurrentDispatcher);
                        Dispatcher.PushFrame(frame); timer.Stop();
                    }
                    var reopened = new MainWindow(new BlueJays.App.Services.SettingsStore(historyDirectory), history);
                    if (((MainViewModel)reopened.DataContext).Captions.Single().Original != "保存した会話") throw new Exception("Window autosave/startup restoration failed.");
                }
                finally { if (Directory.Exists(historyDirectory)) Directory.Delete(historyDirectory, true); }
                Localizer.Current.SetLanguage("en");
                var vm = new MainViewModel();
                var conversation = new ConversationViewModel(new() { Name = "Project planning · English / 日本語", TargetLanguages = ["Japanese", "English"] });
                vm.Conversations.Add(conversation); vm.SelectedConversation = conversation;
                vm.Add(new("one", "Meeting audio", DateTimeOffset.Now, "現在時間還很充沛，等下可以再開一個會議。", "meeting", 2, true));
                vm.ApplyTranslation("one", 2, "Japanese", "時間の余裕はまだありますので、この後再び会議ができます。", "Translated");
                vm.ApplyTranslation("one", 2, "English", "We have plenty of time right now, so we could meet again later.", "Translated");
                vm.Sources.Add(new(new() { Id = "mic", Name = "Alice", DeviceName = "USB microphone", Kind = AudioSourceKind.Microphone }));
                vm.Sources.Add(new(new() { Id = "meeting", Name = "Room participants", DeviceName = "Headphones / Teams audio", Kind = AudioSourceKind.ComputerAudio }));
                vm.Sources.Last().SetStatus("Unavailable · reconnect or choose device");
                var main = new MainWindow { DataContext = vm };
                ((Expander)main.FindName("SourceAdder")).IsExpanded = false;
                Localizer.Current.SetLanguage("en");
                Render(main, 1180, 820, "main");
                Render(main, 980, 620, "main-small");
                Render(new ConversationWindow(new()), 690, 760, "new-conversation");
                Render(new CaptionWindow { DataContext = vm }, 680, 300, "pinned");
                Render(new RoomWindow { DataContext = vm }, 1280, 800, "meeting-room");
                vm.Add(new("corrected", "Alice", DateTimeOffset.Now.AddSeconds(1), "本日の経費精算を確認します。", "mic", 2, true, RawText: "本日の経費生産を確認します。"));
                vm.ApplyTranslation("corrected", 2, "English", "We will review today's expense claims.", "Translated");
                vm.ApplyTranslation("corrected", 2, "Japanese", "本日の経費精算を確認します。", "Translated");
                Render(main, 1180, 820, "main-context-correction");
                Render(new RoomWindow { DataContext = vm }, 1280, 800, "room-context-correction");
                Render(new CaptionWindow { DataContext = vm }, 680, 300, "pinned-context-correction");
                vm.Add(new("pending", "Alice", DateTimeOffset.Now.AddSeconds(3), "The next statement is still being recognized…", "mic", 1, false));
                vm.Recording = true;
                Render(new RoomWindow { DataContext = vm }, 1280, 800, "room-retained-translation");
                vm.Recording = false;
                foreach (var language in Localizer.SupportedLanguages)
                {
                    Localizer.Current.SetLanguage(language);
                    var settings = new SettingsWindow(new(), "");
                    vm.Status = "Vulkan · " + Localizer.F("STT delay: {0:F1}s", 2.4) + " · " + Localizer.F("Listening · {0}/{1} audio sources", 1, 2);
                    Render(main, 980, 620, "main-small-" + language);
                    Render(new ConversationWindow(new()), 690, 760, "conversation-" + language);
                    Render(new AudioSourceDeviceWindow(new() { Name = "Room participants", DeviceId = "old", Kind = AudioSourceKind.ComputerAudio },
                        [new("available", "Speakers / Teams output", AudioSourceKind.ComputerAudio)]), 650, 350, "choose-device-" + language);
                    var tabs = (TabControl)settings.FindName("SettingsTabs");
                    for (var tab = 0; tab < tabs.Items.Count; tab++)
                    {
                        tabs.SelectedIndex = tab;
                        Render(settings, 740, 790, $"settings-{language}-{tab}");
                    }
                    Render(new AboutWindow(), 660, 740, "about-" + language);
                    vm.PriorityLanguage = "English";
                    Render(new CaptionWindow { DataContext = vm }, 680, 360, "pinned-english-" + language);
                    vm.PriorityLanguage = "";
                    Render(new CaptionWindow { DataContext = vm }, 680, 360, "pinned-original-" + language);
                    foreach (var engine in new[] { SpeechEngine.SenseVoice, SpeechEngine.Qwen3Asr })
                    {
                        var engineSettings = new SettingsWindow(new() { SpeechEngine = engine }, "");
                        ((TabControl)engineSettings.FindName("SettingsTabs")).SelectedIndex = 1;
                        Render(engineSettings, 740, 790, $"settings-{language}-{engine}");
                    }
                    if (Localizer.T("Settings") == "Settings" && language != "en") throw new Exception("UI language did not switch.");
                }
                Localizer.Current.SetLanguage("en");
                var settingsData = Directory.CreateTempSubdirectory("BlueJays-settings-ui-");
                try
                {
                    var dictionaries = new TerminologyStore(settingsData.FullName);
                    var usage = new TokenUsageStore(settingsData.FullName);
                    var today = DateOnly.FromDateTime(DateTime.Now);
                    for (var day = 0; day < 14; day++)
                        usage.Record(new(800 + 120 * (day % 5), 300, 140), today.AddDays(-day));
                    var settings = new SettingsWindow(new(), "", dictionaries, usage);
                    var tabs = (TabControl)settings.FindName("SettingsTabs");
                    tabs.SelectedIndex = 3;
                    var terminology = (TerminologyView)settings.FindName("TerminologyTab");
                    ((TextBox)terminology.FindName("NameBox")).Text = "Project terms";
                    ((Button)terminology.FindName("SaveDictionaryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ((TextBox)terminology.FindName("TermBox")).Text = "経費精算";
                    ((ComboBox)terminology.FindName("LanguageBox")).SelectedValue = "ja";
                    ((TextBox)terminology.FindName("ReadingBox")).Text = "けいひせいさん";
                    ((TextBox)terminology.FindName("MeaningBox")).Text = "Expense claims";
                    ((Button)terminology.FindName("SaveEntryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var saved = dictionaries.Load().Single();
                    if (saved.Entries.Single().Reading != "けいひせいさん") throw new Exception("Terminology editor did not persist the Japanese reading.");
                    ((ListBox)terminology.FindName("EntriesBox")).SelectedIndex = 0;
                    Render(settings, 740, 790, "settings-terminology-populated");
                    var newConversation = new ConversationWindow(new(), [saved]);
                    var choices = (ComboBox)newConversation.FindName("DictionaryBox");
                    if (choices.Items.Count != 2) throw new Exception("The saved dictionary was not available for new conversations.");
                    choices.SelectedIndex = 1;
                    Render(newConversation, 690, 760, "conversation-terminology");
                    tabs.SelectedIndex = 4;
                    var tokenUsage = (TokenUsageView)settings.FindName("TokenUsageTab");
                    ((ComboBox)tokenUsage.FindName("ProviderBox")).SelectedItem = "DeepSeek";
                    ((TextBox)tokenUsage.FindName("InputBox")).Text = "1000000";
                    ((TextBox)tokenUsage.FindName("CachedBox")).Text = "500000";
                    ((TextBox)tokenUsage.FindName("OutputBox")).Text = "250000";
                    var expected = Localizer.F("Estimated cost · USD {0}", 0.2265m.ToString("0.00####"));
                    if (((TextBlock)tokenUsage.FindName("QuoteText")).Text != expected) throw new Exception("The DeepSeek quote did not update when entering tokens.");
                    Render(settings, 740, 790, "settings-token-usage-populated");
                }
                finally { settingsData.Delete(recursive: true); }
                foreach (var file in Directory.GetFiles(Path.Combine(Directory.GetCurrentDirectory(), "src", "BlueJays.App"), "*.xaml", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
                    foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\{loc:Tr (\w+)\}"))
                        if (!Localizer.HasTranslation(match.Groups[1].Value)) throw new Exception("Missing XAML UI translation: " + match.Value);
                vm.SelectedConversation = new(new() { TargetLanguages = ["Japanese", "English", "French", "Korean"] });
                Render(new RoomWindow { DataContext = vm }, 1280, 800, "room-four-languages");
                vm.SelectedConversation = new(new() { TargetLanguages = [] });
                Render(new RoomWindow { DataContext = vm }, 1000, 700, "room-transcription-only");
                Render(new SettingsWindow(new(), ""), 740, 790, "settings");
                if (trace.Messages.Count > 0) throw new Exception(string.Join(Environment.NewLine, trace.Messages));
                done.TrySetResult();

                void Render(Window window, double width, double height, string name)
                {
                    var content = (FrameworkElement)window.Content;
                    content.Measure(new(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
                    var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
                    var background = new DrawingVisual();
                    using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
                    var image = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                    image.Render(background); image.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(output);
                }
                // Do not close these unshown windows: MainWindow's close handler persists user settings.
            }
            catch (Exception ex) { done.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
