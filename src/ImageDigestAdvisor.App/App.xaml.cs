using ImageDigestAdvisor.App.Services;
using ImageDigestAdvisor.Core;
using ImageDigestAdvisor.Core.Credentials;
using ImageDigestAdvisor.Core.Registry;
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
        Services = new ImageDigestAdvisorServices(new WindowsCredentialStore(), Cache);
    }

    public static new App Current => (App)Application.Current;

    public ImageDigestAdvisorServices Services { get; }

    public DiskContentCache Cache { get; }

    public AppSettings Settings { get; }

    public Window MainWindow => _window!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>Writes %LOCALAPPDATA%\ImageDigestAdvisor\crash.log so failures on other machines can be diagnosed.</summary>
    private static void WriteCrashLog(Exception? exception)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageDigestAdvisor");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
