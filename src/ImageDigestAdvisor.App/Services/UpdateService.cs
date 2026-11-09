using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Velopack;
using Velopack.Sources;

namespace ImageDigestAdvisor.App.Services;

public enum UpdateState
{
    Idle,
    Checking,
    Downloading,
    UpToDate,
    ReadyToInstall,
    NotSupported,
    Failed,
}

/// <summary>
/// Automatic updates through Velopack and GitHub Releases. Only active when the app was installed
/// with Setup.exe; the MSIX/Store build is updated by the Store and dev builds are never updated.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    public const string RepositoryUrl = "https://github.com/hvetter-de/Image-Digest-Advisor";

    /// <summary>For testing: point updates at a local folder with vpk output instead of GitHub.</summary>
    private const string SourceOverrideVariable = "IMAGEDIGESTADVISOR_UPDATE_SOURCE";

    private readonly UpdateManager? _manager;
    private readonly DispatcherQueue _dispatcher;
    private UpdateInfo? _update;

    public UpdateService()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        try
        {
            var overrideSource = Environment.GetEnvironmentVariable(SourceOverrideVariable);
            IUpdateSource source = string.IsNullOrWhiteSpace(overrideSource)
                ? new GithubSource(RepositoryUrl, accessToken: null, prerelease: false)
                : new SimpleFileSource(new DirectoryInfo(overrideSource));
            _manager = new UpdateManager(source);
        }
        catch (Exception)
        {
            _manager = null;
        }

        if (!IsInstalled)
        {
            State = UpdateState.NotSupported;
            StatusText = "Automatic updates are available when the app is installed with Setup.exe.";
        }
        else if (_manager!.UpdatePendingRestart is { } pending)
        {
            State = UpdateState.ReadyToInstall;
            AvailableVersion = pending.Version.ToString();
            StatusText = $"Version {AvailableVersion} has been downloaded and will be installed on restart.";
        }
    }

    /// <summary>True for Setup.exe installs and the Velopack portable zip; false for MSIX and dev builds.</summary>
    public bool IsInstalled => _manager?.IsInstalled == true;

    public string CurrentVersion =>
        _manager is { IsInstalled: true, CurrentVersion: { } v }
            ? v.ToString()
            : Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
              ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
              ?? "unknown";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadyToInstall), nameof(IsBusy), nameof(CanCheck))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(RestartAndUpdateCommand))]
    public partial UpdateState State { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? AvailableVersion { get; set; }

    [ObservableProperty]
    public partial int Progress { get; set; }

    public bool IsReadyToInstall => State == UpdateState.ReadyToInstall;

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;

    public bool CanCheck => IsInstalled && !IsBusy && State != UpdateState.ReadyToInstall;

    /// <summary>Checks GitHub Releases and downloads a newer version in the background.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    public async Task CheckAsync()
    {
        if (_manager is null || !IsInstalled) return;

        try
        {
            State = UpdateState.Checking;
            StatusText = "Checking for updates…";

            _update = await _manager.CheckForUpdatesAsync();
            if (_update is null)
            {
                State = UpdateState.UpToDate;
                StatusText = $"You have the latest version ({CurrentVersion}). Last checked {DateTime.Now:t}.";
                return;
            }

            AvailableVersion = _update.TargetFullRelease.Version.ToString();
            State = UpdateState.Downloading;
            StatusText = $"Downloading version {AvailableVersion}…";

            await _manager.DownloadUpdatesAsync(_update, p => _dispatcher.TryEnqueue(() => Progress = p));

            State = UpdateState.ReadyToInstall;
            StatusText = $"Version {AvailableVersion} is ready. Restart the app to install it (or it is installed automatically next time you start the app).";
        }
        catch (Exception e)
        {
            State = UpdateState.Failed;
            StatusText = $"Update check failed: {e.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsReadyToInstall))]
    private void RestartAndUpdate()
    {
        var asset = _update?.TargetFullRelease ?? _manager?.UpdatePendingRestart;
        if (_manager is null || asset is null) return;
        _manager.ApplyUpdatesAndRestart(asset);
    }
}
