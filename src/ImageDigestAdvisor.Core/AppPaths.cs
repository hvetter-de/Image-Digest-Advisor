namespace ImageDigestAdvisor.Core;

/// <summary>
/// Where the app keeps its data. Deliberately not <c>%LOCALAPPDATA%\ImageDigestAdvisor</c>: that is the
/// installation folder used by Setup.exe (Velopack), which is replaced on updates and removed on uninstall.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageDigestAdvisor.Data");

    public static string CacheDirectory => Path.Combine(DataDirectory, "cache");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string CrashLog => Path.Combine(DataDirectory, "crash.log");
}
