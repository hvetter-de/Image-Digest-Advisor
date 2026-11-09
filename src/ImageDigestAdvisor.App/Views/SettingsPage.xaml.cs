using System.Diagnostics;
using System.Reflection;
using ImageDigestAdvisor.App.ViewModels;
using ImageDigestAdvisor.Core.Credentials;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ImageDigestAdvisor.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public Services.UpdateService Updates => App.Current.Updates;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshCacheInfo();

        var configPath = DockerConfigCredentialProvider.DefaultConfigPath();
        DockerInfo.Text = File.Exists(configPath)
            ? $"Docker configuration found at {configPath}. Logins from 'docker login' are used automatically."
            : "No Docker configuration found. That's fine: Docker is not required. Add accounts under Registry accounts if needed.";

        VersionInfo.Text = $"Image Digest Advisor {Updates.CurrentVersion}";
        AutoUpdateToggle.IsOn = App.Current.Settings.AutoCheckUpdates;
    }

    private void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var settings = App.Current.Settings;
        if (settings.AutoCheckUpdates == AutoUpdateToggle.IsOn) return;
        settings.AutoCheckUpdates = AutoUpdateToggle.IsOn;
        settings.Save();
    }

    private void RefreshCacheInfo()
    {
        var dir = App.Current.Cache.RootDirectory;
        long size = 0;
        var count = 0;
        if (Directory.Exists(dir))
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                size += f.Length;
                count++;
            }
        }
        CacheInfo.Text = $"{dir}\n{count} entries, {ImageCardViewModel.FormatSize(size)}";
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Current.Cache.Clear();
        }
        catch (IOException)
        {
        }
        RefreshCacheInfo();
    }

    private void OpenCache_Click(object sender, RoutedEventArgs e)
    {
        var dir = App.Current.Cache.RootDirectory;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
