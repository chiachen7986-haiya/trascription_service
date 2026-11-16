using System.Windows;

namespace BlueJays.App;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        // Two writers must not overwrite each other's conversation archives.
        instance = new Mutex(true, @"Local\TranscriptionService.Rosedale", out ownsInstance);
        if (!ownsInstance)
        {
            MessageBox.Show("Transcription Service is already running.", "Transcription Service");
            Shutdown(); return;
        }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstance) instance?.ReleaseMutex();
        instance?.Dispose(); base.OnExit(e);
    }
}
