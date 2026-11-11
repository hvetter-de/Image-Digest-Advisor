using ImageDigestAdvisor.App.Services;
using ImageDigestAdvisor.Core;
using ImageDigestAdvisor.Core.Credentials;
using ImageDigestAdvisor.Core.Registry;
using ImageDigestAdvisor.Core.Security;
using Microsoft.UI.Xaml;

namespace ImageDigestAdvisor.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => WriteCrashLog(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject as Exception);

        Settings = AppSettings.Load();
        Cache = new DiskContentCache(DiskContentCache.DefaultDirectory());
        Services = new ImageDigestAdvisorServices(new WindowsCredentialStore(), Cache,
            vulnerabilityCache: new FileTimedCache(Path.Combine(AppPaths.CacheDirectory, "osv")));
    }

    public static new App Current => (App)Application.Current;

    public ImageDigestAdvisorServices Services { get; }

    public DiskContentCache Cache { get; }

    public AppSettings Settings { get; }

    public Window MainWindow => _window!;

    public UpdateService Updates { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Updates = new UpdateService();
        _window = new MainWindow();
        _window.Activate();

        if (Settings.AutoCheckUpdates && Updates.CanCheck)
        {
            // Give the UI a moment to settle, then check and download in the background.
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => Updates.CheckAsync(), TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>Writes crash.log to the data folder so failures on other machines can be diagnosed.</summary>
    private static void WriteCrashLog(Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(AppPaths.CrashLog, $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
