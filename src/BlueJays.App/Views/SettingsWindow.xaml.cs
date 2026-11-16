using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BlueJays.App.Services;
using BlueJays.App.ViewModels;
using BlueJays.Core.Models;
using BlueJays.Core.Translation;
using Microsoft.Win32;
using L = BlueJays.App.Localization.Localizer;

namespace BlueJays.App.Views;

public partial class SettingsWindow : Window
{
    private sealed class SpeechLanguageOption(string code, string name) : ObservableObject
    {
        public string Code => code;
        public string DisplayName => L.T(name);
    }
    private sealed record UiLanguageOption(string Code, string Name);
    private sealed record EngineOption(SpeechEngine Engine, string Label) { public string Name => L.T(Label); }
    private sealed record DeviceOption(SpeechDevice Device, string Label) { public string Name => L.T(Label); }
    private readonly AppSettings original;
    private readonly Dictionary<SpeechEngine, string> modelPaths = new();
    private SpeechEngine selectedEngine;
    private bool initialized;
    private CancellationTokenSource? downloadCancellation;
    private bool closeAfterDownload;
    public AppSettings Result { get; private set; }
    public string ApiKey { get; private set; }

    public SettingsWindow(AppSettings settings, string apiKey)
    {
        original = Result = settings;
        ApiKey = apiKey;
        InitializeComponent();
        MicNoiseBox.IsChecked = settings.MicrophoneNoiseSuppression;
        WindowSizing.FitToWorkArea(this);
        UiLanguageBox.ItemsSource = new UiLanguageOption[] { new("en", "English"), new("ja", "日本語"), new("zh-Hans", "中文（简体）"), new("zh-Hant", "中文（繁體）") };
        UiLanguageBox.SelectedValue = settings.UiLanguage;
        if (UiLanguageBox.SelectedIndex < 0) UiLanguageBox.SelectedIndex = 0;
        ModelPathBox.Text = settings.ModelPath;
        modelPaths[SpeechEngine.Whisper] = settings.ModelPath;
        modelPaths[SpeechEngine.SenseVoice] = SpeechModels.PathFor(settings with { SpeechEngine = SpeechEngine.SenseVoice });
        modelPaths[SpeechEngine.Qwen3Asr] = SpeechModels.PathFor(settings with { SpeechEngine = SpeechEngine.Qwen3Asr });
        SpeechEngineBox.ItemsSource = new EngineOption[]
        {
            new(SpeechEngine.Whisper, "Whisper (Vulkan / CPU)"), new(SpeechEngine.SenseVoice, "SenseVoice Small INT8"),
            new(SpeechEngine.Qwen3Asr, "Qwen3-ASR 0.6B INT8")
        };
        SpeechDeviceBox.ItemsSource = new DeviceOption[]
        {
            new(SpeechDevice.OpenVinoGpu, "OpenVINO · Intel GPU"), new(SpeechDevice.OpenVinoCpu, "OpenVINO · CPU"), new(SpeechDevice.Cpu, "ONNX Runtime · CPU")
        };
        SpeechDeviceBox.SelectedValue = settings.SpeechDevice;
        CpuFallbackBox.IsChecked = settings.AllowSpeechCpuFallback;
        CpuThreadsBox.ItemsSource = new[] { 1, 2, 4, 6, 8, 12 };
        CpuThreadsBox.SelectedItem = new[] { 1, 2, 4, 6, 8, 12 }.Contains(settings.SpeechCpuThreads) ? settings.SpeechCpuThreads : 6;
        QwenPreviewsBox.IsChecked = settings.QwenLivePreviews;
        SpeechLanguageBox.ItemsSource = new SpeechLanguageOption[]
        {
            new("auto", "Detect automatically"), new("en", "English"), new("ja", "Japanese"),
            new("zh", "Chinese"), new("ko", "Korean"), new("es", "Spanish"), new("fr", "French"),
            new("de", "German"), new("it", "Italian"), new("pt", "Portuguese"), new("ru", "Russian"),
            new("ar", "Arabic"), new("hi", "Hindi"), new("th", "Thai"), new("vi", "Vietnamese"),
            new("id", "Indonesian"), new("nl", "Dutch"), new("pl", "Polish"), new("uk", "Ukrainian"),
            new("tr", "Turkish"), new("sv", "Swedish"), new("fi", "Finnish"), new("da", "Danish"), new("no", "Norwegian")
        };
        SpeechLanguageBox.SelectedValue = settings.SpeechLanguage;
        if (SpeechLanguageBox.SelectedIndex < 0) SpeechLanguageBox.SelectedIndex = 0;
        TargetLanguageBox.ItemsSource = LanguageChoice.Create();
        TargetLanguageBox.SelectedValue = settings.TargetLanguage;
        if (TargetLanguageBox.SelectedIndex < 0) TargetLanguageBox.SelectedIndex = 0;
        BaseUrlBox.Text = settings.ApiBaseUrl;
        ApiModelBox.Text = settings.ApiModel;
        ApiKeyBox.Password = apiKey;
        ThinkingDisabledBox.IsChecked = settings.SendThinkingDisabled;
        CorrectionBox.IsChecked = settings.ContextualCorrectionEnabled;
        NeuralDetectionBox.IsChecked = settings.NeuralSpeechDetection;
        BeamSearchBox.IsChecked = settings.UseBeamSearch;
        GpuAccelerationBox.IsChecked = settings.UseGpuAcceleration;
        ParallelDecodingBox.IsChecked = settings.ParallelLanguageDecoding;
        SilenceSlider.Value = settings.SilenceMilliseconds;
        PhraseSlider.Value = settings.MaxPhraseSeconds;
        ThresholdSlider.Value = settings.SpeechThreshold;
        initialized = true;
        SpeechEngineBox.SelectedValue = settings.SpeechEngine;
        RefreshEngine();
        Closing += OnClosing;
    }

    private void Engine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || SpeechEngineBox.SelectedValue is not SpeechEngine engine) return;
        modelPaths[selectedEngine] = ModelPathBox.Text.Trim(); selectedEngine = engine;
        ModelPathBox.Text = modelPaths[engine]; RefreshEngine();
    }

    private void RefreshEngine()
    {
        var whisper = selectedEngine == SpeechEngine.Whisper;
        var device = SpeechDeviceBox.SelectedValue is SpeechDevice chosen ? chosen : original.SpeechDevice;
        SpeechDeviceBox.ItemsSource = new DeviceOption[]
        {
            new(SpeechDevice.OpenVinoGpu, "OpenVINO · Intel GPU"), new(SpeechDevice.OpenVinoCpu, "OpenVINO · CPU"), new(SpeechDevice.Cpu, "ONNX Runtime · CPU")
        }.Where(option => selectedEngine != SpeechEngine.Qwen3Asr || option.Device != SpeechDevice.OpenVinoGpu).ToArray();
        SpeechDeviceBox.SelectedValue = selectedEngine == SpeechEngine.Qwen3Asr && device == SpeechDevice.OpenVinoGpu ? SpeechDevice.OpenVinoCpu : device;
        ComputeHelp.Text = L.T(selectedEngine == SpeechEngine.Qwen3Asr
            ? "Qwen GPU is unavailable in this build. Select OpenVINO CPU or ONNX Runtime CPU."
            : "CPU is the fastest SenseVoice option on the tested Iris Xe laptop. First OpenVINO startup compiles kernels and can take about a minute.");
        WhisperOptions.Visibility = WhisperAccuracyOptions.Visibility = BundledWhisperButtons.Visibility = ModelSizeBox.Visibility = whisper ? Visibility.Visible : Visibility.Collapsed;
        OnnxOptions.Visibility = whisper ? Visibility.Collapsed : Visibility.Visible;
        QwenPreviewsBox.Visibility = selectedEngine == SpeechEngine.Qwen3Asr ? Visibility.Visible : Visibility.Collapsed;
        ModelPathLabel.Text = L.T(whisper ? "Model file" : "Model folder");
        EngineHelp.Text = L.T(selectedEngine switch
        {
            SpeechEngine.SenseVoice => "Fast multilingual recognition for Japanese, Chinese, English, Korean and Cantonese. English accuracy can be lower; compare with Qwen or Whisper.",
            SpeechEngine.Qwen3Asr => "Multilingual quality option. Final phrases avoid repeated decoding. OpenVINO runs the audio encoder; the frontend and text decoder stay on CPU for compatibility.",
            _ => "Whisper uses Vulkan acceleration and fixed-language workers. Smaller models respond faster."
        });
        DownloadStatus.Text = whisper ? L.T("Download source: Hugging Face / ggerganov/whisper.cpp") :
            L.F("Official sherpa-onnx export · {0} MB download · SHA-256 verified", SpeechModels.Package(selectedEngine).DownloadBytes / 1_000_000);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (selectedEngine != SpeechEngine.Whisper)
        {
            var folder = new OpenFolderDialog { Title = L.T("Model folder") };
            if (folder.ShowDialog(this) == true) ModelPathBox.Text = folder.FolderName;
            return;
        }
        var dialog = new OpenFileDialog { Filter = L.T("Whisper GGML model (*.bin)|*.bin"), CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) ModelPathBox.Text = dialog.FileName;
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (downloadCancellation is not null) return;
        downloadCancellation = new();
        DownloadButton.IsEnabled = SaveButton.IsEnabled = SpeechEngineBox.IsEnabled = false;
        CancelDownloadButton.Visibility = Visibility.Visible;
        ErrorText.Text = "";
        try
        {
            var size = ((ComboBoxItem)ModelSizeBox.SelectedItem).Tag.ToString()!;
            DownloadStatus.Text = L.T("Downloading model…");
            var progress = new Progress<double>(value =>
            {
                DownloadProgress.IsIndeterminate = value < 0;
                if (value >= 0) DownloadProgress.Value = value;
                DownloadStatus.Text = value < 0 ? L.T("Downloading model…") : L.F("Downloading model… {0}", value.ToString("P0"));
            });
            ModelPathBox.Text = selectedEngine == SpeechEngine.Whisper
                ? await ModelDownloader.DownloadAsync(size, progress, downloadCancellation.Token)
                : await SpeechModelDownloader.DownloadAsync(selectedEngine, progress, downloadCancellation.Token);
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 1;
            DownloadStatus.Text = L.T("Model ready. Save settings to use it.");
        }
        catch (OperationCanceledException) { DownloadStatus.Text = L.T("Download canceled."); }
        catch (Exception ex) { ErrorText.Text = L.F("Download failed: {0}", ex.Message); }
        finally
        {
            downloadCancellation.Dispose();
            downloadCancellation = null;
            DownloadProgress.IsIndeterminate = false;
            DownloadButton.IsEnabled = SaveButton.IsEnabled = SpeechEngineBox.IsEnabled = true;
            CancelDownloadButton.Visibility = Visibility.Collapsed;
            if (closeAfterDownload) Close();
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => downloadCancellation?.Cancel();
    private void AccuracyModel_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(SettingsStore.AccuracyModelPath)) ModelPathBox.Text = SettingsStore.AccuracyModelPath;
        else { ModelSizeBox.SelectedIndex = 1; ErrorText.Text = L.T("Download the recommended model, or browse to a company-staged copy."); }
    }
    private void BalancedModel_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(SettingsStore.BalancedModelPath)) ModelPathBox.Text = SettingsStore.BalancedModelPath;
        else { ModelSizeBox.SelectedIndex = 0; ErrorText.Text = L.T("Download the recommended model, or browse to a company-staged copy."); }
    }
    private void DeepSeek_Click(object sender, RoutedEventArgs e)
    { BaseUrlBox.Text = "https://api.deepseek.com/v1"; ApiModelBox.Text = "deepseek-flash"; ThinkingDisabledBox.IsChecked = true; }
    private void Generic_Click(object sender, RoutedEventArgs e)
    { BaseUrlBox.Text = "http://localhost:1234/v1"; ApiModelBox.Text = "your-model-id"; ThinkingDisabledBox.IsChecked = false; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = ModelPathBox.Text.Trim();
            if (selectedEngine == SpeechEngine.Whisper && path.Length > 0 && !File.Exists(path)) throw new ArgumentException(L.T("The model file does not exist."));
            if (selectedEngine != SpeechEngine.Whisper && SpeechModels.MissingFiles(selectedEngine, path).Length > 0)
                throw new ArgumentException(L.T("Select a complete model folder or download the model first."));
            modelPaths[selectedEngine] = path.Length == 0 ? "" : Path.GetFullPath(path);
            if (!string.IsNullOrWhiteSpace(BaseUrlBox.Text))
            {
                _ = TranslationClient.GetEndpoint(BaseUrlBox.Text.Trim());
                if (string.IsNullOrWhiteSpace(ApiModelBox.Text) || TargetLanguageBox.SelectedValue is null)
                    throw new ArgumentException(L.T("Enter a translation model and target language."));
            }
            Result = original with
            {
                MicrophoneNoiseSuppression = MicNoiseBox.IsChecked == true,
                ModelPath = modelPaths[SpeechEngine.Whisper], SpeechEngine = selectedEngine,
                SenseVoiceModelPath = modelPaths[SpeechEngine.SenseVoice], QwenModelPath = modelPaths[SpeechEngine.Qwen3Asr],
                SpeechDevice = (SpeechDevice)SpeechDeviceBox.SelectedValue,
                AllowSpeechCpuFallback = CpuFallbackBox.IsChecked == true, SpeechCpuThreads = (int)CpuThreadsBox.SelectedItem,
                QwenLivePreviews = QwenPreviewsBox.IsChecked == true,
                UiLanguage = (string)UiLanguageBox.SelectedValue,
                SpeechLanguage = (string)SpeechLanguageBox.SelectedValue,
                TranslationEnabled = true,
                TargetLanguage = (string)TargetLanguageBox.SelectedValue, ApiBaseUrl = BaseUrlBox.Text.Trim(), ApiModel = ApiModelBox.Text.Trim(),
                SendThinkingDisabled = ThinkingDisabledBox.IsChecked == true,
                ContextualCorrectionEnabled = CorrectionBox.IsChecked == true,
                NeuralSpeechDetection = NeuralDetectionBox.IsChecked == true, UseBeamSearch = BeamSearchBox.IsChecked == true,
                UseGpuAcceleration = GpuAccelerationBox.IsChecked == true, ParallelLanguageDecoding = ParallelDecodingBox.IsChecked == true,
                SilenceMilliseconds = (int)SilenceSlider.Value, MaxPhraseSeconds = (int)PhraseSlider.Value,
                SpeechThreshold = ThresholdSlider.Value
            };
            ApiKey = ApiKeyBox.Password.Trim();
            DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (downloadCancellation is null) return;
        downloadCancellation.Cancel();
        e.Cancel = true;
        closeAfterDownload = true;
        ErrorText.Text = L.T("Canceling download…");
    }
}

