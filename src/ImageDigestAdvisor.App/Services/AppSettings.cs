using System.Text.Json;

namespace ImageDigestAdvisor.App.Services;

/// <summary>
/// User preferences stored as JSON under %LOCALAPPDATA%\ImageDigestAdvisor. This works for both the
/// unpackaged build and the MSIX/Store package (where the path is transparently redirected).
/// </summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Core.AppPaths.SettingsFile;

    public string Platform { get; set; } = "linux/amd64";

    /// <summary>Flag images older than this many days. 0 disables the check.</summary>
    public int MaxAgeDays { get; set; } = 90;

    public List<string> RecentInputs { get; set; } = [];

    /// <summary>Check GitHub Releases for a new version when the app starts (Setup.exe installs only).</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>
    /// Check images for known vulnerabilities (SBOM from the registry + osv.dev). Off by default because it
    /// sends package names and versions to osv.dev.
    /// </summary>
    public bool CheckVulnerabilities { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void AddRecent(string input)
    {
        RecentInputs.Remove(input);
        RecentInputs.Insert(0, input);
        if (RecentInputs.Count > 15) RecentInputs.RemoveRange(15, RecentInputs.Count - 15);
    }
}
