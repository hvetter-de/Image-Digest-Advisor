using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageDigestAdvisor.Core.Analysis;
using Windows.ApplicationModel.DataTransfer;

namespace ImageDigestAdvisor.App.ViewModels;

public sealed record PropertyRow(string Name, string Value, bool Monospace = false, string? Link = null, bool Emphasis = false)
{
    public bool HasLink => Link is not null;

    public bool HasNoLink => Link is null;

    public Uri? LinkUri => Link is not null && Uri.TryCreate(Link, UriKind.Absolute, out var u) ? u : null;
}

/// <summary>A collapsible group of rows on a card (labels, environment, build history).</summary>
public sealed record DetailSection(string Header, IReadOnlyList<PropertyRow> Rows);

/// <summary>Display model for one image card (the requested image or a suggestion).</summary>
public sealed partial class ImageCardViewModel : ObservableObject
{
    /// <param name="compareTo">For suggestions: the user's image, to show what changed.</param>
    public ImageCardViewModel(string title, string subtitle, string pinnedReference, ImageDetails details, DateTimeOffset now,
        bool isRecommended = false, ImageDetails? compareTo = null)
    {
        Title = title;
        Subtitle = subtitle;
        PinnedReference = pinnedReference;
        IsRecommended = isRecommended;
        Properties = BuildProperties(details, now, compareTo);
        Sections = BuildSections(details);
    }

    public string Title { get; }

    public string Subtitle { get; }

    public string PinnedReference { get; }

    public bool IsRecommended { get; }

    public IReadOnlyList<PropertyRow> Properties { get; }

    public IReadOnlyList<DetailSection> Sections { get; }

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

    private static List<PropertyRow> BuildProperties(ImageDetails d, DateTimeOffset now, ImageDetails? compareTo)
    {
        var rows = new List<PropertyRow>
        {
            new("Created", d.Created is { } c ? $"{FormatTime(c)}  ·  {FormatAge(now - c)}" : "unknown"),
        };

        if (compareTo is not null && d.Created is { } newCreated && compareTo.Created is { } oldCreated)
        {
            var days = (int)Math.Round((newCreated - oldCreated).TotalDays);
            var relation = days switch
            {
                > 0 => $"{days} days newer",
                < 0 => $"{-days} days older",
                _ => "same day",
            };
            rows.Add(new("Replaces", $"your build from {oldCreated.UtcDateTime:yyyy-MM-dd HH:mm} UTC ({relation})"));
        }

        // Software versions the image declares (DOTNET_VERSION, NGINX_VERSION, ...).
        var software = d.SoftwareVersions;
        if (software.Count > 0)
        {
            var width = software.Max(v => v.DisplayName.Length);
            rows.Add(new("Software", string.Join("\n", software.Select(v => $"{v.DisplayName.PadRight(width)}  {v.Value}")), Monospace: true));
        }

        if (compareTo is not null && (software.Count > 0 || compareTo.SoftwareVersions.Count > 0))
        {
            var changes = d.SoftwareChangesSince(compareTo);
            rows.Add(changes.Count > 0
                ? new("Changes vs. yours", string.Join("\n", changes), Emphasis: true)
                : new("Changes vs. yours", "Same software versions – rebuilt with base image / OS updates"));
        }

        var platforms = d.AvailablePlatforms.Count > 1
            ? string.Join("  ·  ", d.AvailablePlatforms.Select(p => p.Matches(d.Platform) ? $"{p} (shown)" : p.ToString()))
            : d.Platform.ToString();
        rows.Add(new(d.AvailablePlatforms.Count > 1 ? $"Platforms ({d.AvailablePlatforms.Count})" : "Platform", platforms));
        if (d.Config?.OsVersion is { } osVersion) rows.Add(new("Windows build", osVersion));

        rows.Add(new("Size (compressed)", FormatSize(d.CompressedSize)));
        rows.Add(new("Layers", d.LayerCount.ToString(CultureInfo.CurrentCulture)));

        if (d.Tags.Count > 0)
        {
            rows.Add(new("Tags", d.Tags.Count > 12 ? string.Join(", ", d.Tags.Take(12)) + $" … (+{d.Tags.Count - 12})" : string.Join(", ", d.Tags)));
        }

        if (d.IndexDigest is not null) rows.Add(new("Index digest (multi-arch)", d.IndexDigest, Monospace: true));
        rows.Add(new(d.IndexDigest is null ? "Digest" : "Platform manifest digest", d.ManifestDigest, Monospace: true));

        if (d.Label("org.opencontainers.image.version") is { } version) rows.Add(new("Version label", version));
        if (d.Label("org.opencontainers.image.source") is { } source) rows.Add(new("Source", source, Link: source.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? source : null));
        if (d.Label("org.opencontainers.image.revision") is { } revision) rows.Add(new("Revision", revision, Monospace: true));
        if (d.Label("org.opencontainers.image.base.name") is { } baseName) rows.Add(new("Base image", baseName));

        if (d.Config is { } config)
        {
            rows.Add(new("Runs as user", config.User ?? "root (not set)"));
            if (config.WorkingDir is { } wd) rows.Add(new("Working directory", wd, Monospace: true));
            var command = string.Join(" ", config.Entrypoint.Concat(config.Cmd));
            if (command.Length > 0) rows.Add(new("Starts with", command, Monospace: true));
            if (config.ExposedPorts.Count > 0) rows.Add(new("Exposed ports", string.Join(", ", config.ExposedPorts)));
        }

        return rows;
    }

    private static List<DetailSection> BuildSections(ImageDetails d)
    {
        var sections = new List<DetailSection>();

        if (d.Labels.Count > 0)
        {
            sections.Add(new($"All labels ({d.Labels.Count})",
                d.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => new PropertyRow(l.Key, l.Value)).ToList()));
        }

        if (d.Config is { Environment.Count: > 0 } envConfig)
        {
            sections.Add(new($"Environment variables ({envConfig.Environment.Count})",
                envConfig.Environment.Select(e => new PropertyRow(e.Key, e.Value, Monospace: true)).ToList()));
        }

        if (d.Config is { History.Count: > 0 } historyConfig)
        {
            var steps = historyConfig.History
                .Select((h, i) => new PropertyRow(
                    $"#{i + 1}  {(h.Created is { } hc ? hc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty)}".TrimEnd(),
                    CleanCommand(h.CreatedBy) + (h.EmptyLayer ? string.Empty : "   [layer]"),
                    Monospace: true))
                .ToList();
            sections.Add(new($"Build history ({steps.Count} steps)", steps));
        }

        return sections;
    }

    /// <summary>Turns <c>/bin/sh -c #(nop) ENV X=1</c> into <c>ENV X=1</c> and shortens very long commands.</summary>
    internal static string CleanCommand(string createdBy)
    {
        var text = createdBy.Trim();
        foreach (var prefix in new[] { "/bin/sh -c #(nop) ", "/bin/sh -c ", "cmd /S /C #(nop) ", "cmd /S /C " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = (prefix.Contains("#(nop)") ? string.Empty : "RUN ") + text[prefix.Length..].Trim();
                break;
            }
        }

        // BuildKit style: "RUN /bin/sh -c apt-get update     && ... # buildkit"
        if (text.StartsWith("RUN /bin/sh -c ", StringComparison.Ordinal)) text = "RUN " + text["RUN /bin/sh -c ".Length..];
        if (text.EndsWith("# buildkit", StringComparison.Ordinal)) text = text[..^"# buildkit".Length];

        // Collapse control characters and runs of whitespace.
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            var isSpace = char.IsWhiteSpace(ch) || char.IsControl(ch);
            if (isSpace && lastWasSpace) continue;
            sb.Append(isSpace ? ' ' : ch);
            lastWasSpace = isSpace;
        }
        text = sb.ToString().Trim();
        return text.Length > 500 ? text[..500] + " …" : text;
    }

    /// <summary>Local time with UTC, e.g. <c>2026-10-13 22:47 (20:47 UTC)</c>.</summary>
    public static string FormatTime(DateTimeOffset time) =>
        $"{time.ToLocalTime():yyyy-MM-dd HH:mm} ({time.UtcDateTime:HH:mm} UTC)";

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
