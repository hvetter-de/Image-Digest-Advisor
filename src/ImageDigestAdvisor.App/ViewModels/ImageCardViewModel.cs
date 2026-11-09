using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageDigestAdvisor.Core.Analysis;
using Windows.ApplicationModel.DataTransfer;

namespace ImageDigestAdvisor.App.ViewModels;

public sealed record PropertyRow(string Name, string Value, bool Monospace = false, string? Link = null)
{
    public bool HasLink => Link is not null;

    public bool HasNoLink => Link is null;

    public Uri? LinkUri => Link is not null && Uri.TryCreate(Link, UriKind.Absolute, out var u) ? u : null;
}

/// <summary>Display model for one image card (the requested image or a suggestion).</summary>
public sealed partial class ImageCardViewModel : ObservableObject
{
    public ImageCardViewModel(string title, string subtitle, string pinnedReference, ImageDetails details, DateTimeOffset now, bool isRecommended = false)
    {
        Title = title;
        Subtitle = subtitle;
        PinnedReference = pinnedReference;
        IsRecommended = isRecommended;
        Properties = BuildProperties(details, now);
        Labels = details.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => new PropertyRow(l.Key, l.Value)).ToList();
    }

    public string Title { get; }

    public string Subtitle { get; }

    public string PinnedReference { get; }

    public bool IsRecommended { get; }

    public IReadOnlyList<PropertyRow> Properties { get; }

    public IReadOnlyList<PropertyRow> Labels { get; }

    public bool HasLabels => Labels.Count > 0;

    public string LabelsHeader => $"All labels ({Labels.Count})";

    [ObservableProperty]
    public partial string CopyText { get; set; } = "Copy";

    [RelayCommand]
    private async Task CopyAsync()
    {
        var package = new DataPackage();
        package.SetText(PinnedReference);
        Clipboard.SetContent(package);
        CopyText = "Copied ✓";
        await Task.Delay(1500);
        CopyText = "Copy";
    }

    private static List<PropertyRow> BuildProperties(ImageDetails d, DateTimeOffset now)
    {
        var rows = new List<PropertyRow>
        {
            new("Created", d.Created is { } c ? $"{c.ToLocalTime():yyyy-MM-dd HH:mm}  ({FormatAge(now - c)})" : "unknown"),
            new("Platform", d.AvailablePlatforms.Count > 1 ? $"{d.Platform}  (of {d.AvailablePlatforms.Count} platforms)" : d.Platform.ToString()),
            new("Size (compressed)", FormatSize(d.CompressedSize)),
            new("Layers", d.LayerCount.ToString(CultureInfo.CurrentCulture)),
        };

        if (d.Tags.Count > 0)
        {
            rows.Add(new("Tags", d.Tags.Count > 12 ? string.Join(", ", d.Tags.Take(12)) + $" … (+{d.Tags.Count - 12})" : string.Join(", ", d.Tags)));
        }

        if (d.IndexDigest is not null) rows.Add(new("Index digest (multi-arch)", d.IndexDigest, Monospace: true));
        rows.Add(new(d.IndexDigest is null ? "Digest" : "Platform manifest digest", d.ManifestDigest, Monospace: true));

        if (d.Label("org.opencontainers.image.version") is { } version) rows.Add(new("Version", version));
        if (d.Label("org.opencontainers.image.source") is { } source) rows.Add(new("Source", source, Link: source.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? source : null));
        if (d.Label("org.opencontainers.image.revision") is { } revision) rows.Add(new("Revision", revision, Monospace: true));
        if (d.Label("org.opencontainers.image.base.name") is { } baseName) rows.Add(new("Base image", baseName));

        return rows;
    }

    public static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 365) return $"{age.TotalDays / 365:0.#} years ago";
        if (age.TotalDays >= 2) return $"{(int)age.TotalDays} days ago";
        if (age.TotalHours >= 2) return $"{(int)age.TotalHours} hours ago";
        return "just now";
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };
}
