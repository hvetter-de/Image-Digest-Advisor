using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageDigestAdvisor.Core.Analysis;
using ImageDigestAdvisor.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ImageDigestAdvisor.App.ViewModels;

public enum SecurityChipState
{
    /// <summary>The check is off and this card doesn't offer it.</summary>
    Hidden,

    /// <summary>The check is off; the chip offers to turn it on.</summary>
    Offer,

    Busy,

    Done,
}

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
        Details = details;
        IsSuggestion = compareTo is not null;
        Properties = BuildProperties(details, now, compareTo);
        Sections = BuildSections(details);
    }

    public ImageDetails Details { get; }

    /// <summary>False for the user's own image.</summary>
    public bool IsSuggestion { get; }

    // ---- Known vulnerabilities (chip on the card, details in VulnerabilityDialog) ----

    /// <summary>Set by <see cref="ResultViewModel"/>: whether the check is enabled, running or done.</summary>
    public SecurityChipState SecurityState
    {
        get;
        private set
        {
            if (SetProperty(ref field, value)) RaiseSecurityChanged();
        }
    } = SecurityChipState.Hidden;

    public VulnerabilityReport? Report { get; private set; }

    /// <summary>The report of the user's image, for "fixes N" on suggestions.</summary>
    public VulnerabilityReport? Baseline { get; private set; }

    /// <summary>Vulnerabilities of the user's image that this suggestion no longer has.</summary>
    public IReadOnlyList<VulnerabilityFinding> FixedVulnerabilities =>
        Report is { IsCompleted: true } r && Baseline is { IsCompleted: true } b ? r.FixedComparedTo(b) : [];

    /// <summary>Vulnerabilities with a fix in the user's image that this suggestion still has.</summary>
    public IReadOnlyList<VulnerabilityFinding> StillOpenVulnerabilities =>
        Report is { IsCompleted: true } r && Baseline is { IsCompleted: true } b ? r.StillOpenComparedTo(b) : [];

    /// <summary>Vulnerabilities this suggestion has that the user's image doesn't.</summary>
    public IReadOnlyList<VulnerabilityFinding> NewVulnerabilities =>
        Report is { IsCompleted: true } r && Baseline is { IsCompleted: true } b ? r.NewComparedTo(b) : [];

    public void ShowSecurityOffer() => SecurityState = IsSuggestion ? SecurityChipState.Hidden : SecurityChipState.Offer;

    public void BeginSecurityScan()
    {
        Report = null;
        Baseline = null;
        SecurityState = SecurityChipState.Busy;
    }

    public void SetSecurityReport(VulnerabilityReport report, VulnerabilityReport? baseline)
    {
        Report = report;
        Baseline = baseline;
        SecurityState = SecurityChipState.Done;
        RaiseSecurityChanged();
    }

    public void CancelSecurityScan()
    {
        if (SecurityState == SecurityChipState.Busy) SecurityState = SecurityChipState.Hidden;
    }

    public bool IsSecurityChipVisible => SecurityState is SecurityChipState.Offer or SecurityChipState.Done;

    public bool IsSecurityBusy => SecurityState == SecurityChipState.Busy;

    private SecurityTone Tone => (SecurityState, Report) switch
    {
        (SecurityChipState.Done, { IsCompleted: true }) when FixedVulnerabilities.Count > 0 => SecurityTone.Good,
        (SecurityChipState.Done, { IsCompleted: true, FixableCount: > 0 }) => SecurityTone.Bad,
        (SecurityChipState.Done, { IsCompleted: true }) => SecurityTone.Good,
        _ => SecurityTone.Neutral,
    };

    public string SecurityText => (SecurityState, Report) switch
    {
        (SecurityChipState.Offer, _) => "Check for CVEs",
        (SecurityChipState.Done, { IsCompleted: true }) when FixedVulnerabilities.Count > 0 => $"Fixes {FixedVulnerabilities.Count} CVEs",
        (SecurityChipState.Done, { IsCompleted: true, FixableCount: > 0 } r) => $"{r.FixableCount} fixable CVEs",
        (SecurityChipState.Done, { IsCompleted: true }) => "No fixable CVEs",
        (SecurityChipState.Done, { Status: VulnerabilityScanStatus.NoSbom }) => "No SBOM",
        (SecurityChipState.Done, { Status: VulnerabilityScanStatus.Failed }) => "CVE check failed",
        _ => string.Empty,
    };

    public string SecurityTooltip => (SecurityState, Report) switch
    {
        (SecurityChipState.Offer, _) => "Check this image for known vulnerabilities (CVEs)",
        (SecurityChipState.Done, { IsCompleted: true } r) =>
            $"{r.FixableCount} with a fix available · {r.NoFixCount} without a fix yet · {r.MinorCount} minor"
            + (FixedVulnerabilities.Count > 0 ? $"\nThis image fixes {FixedVulnerabilities.Count} of the vulnerabilities in your image." : string.Empty)
            + "\nClick for details.",
        (SecurityChipState.Done, { Status: VulnerabilityScanStatus.NoSbom }) =>
            "The registry publishes no SBOM (software bill of materials) for this image, so it can't be checked for known vulnerabilities.",
        (SecurityChipState.Done, { Message: { } m }) => m,
        _ => string.Empty,
    };

    /// <summary>Segoe Fluent Icons: Shield / ShieldError-like warning / checkmark.</summary>
    public string SecurityGlyph => Tone switch
    {
        SecurityTone.Bad => "",  // Warning
        SecurityTone.Good => "", // CheckMark
        _ => "",                 // Shield
    };

    public Brush SecurityBackground => Tone switch
    {
        SecurityTone.Bad => ThemeBrush("SystemFillColorCriticalBackgroundBrush"),
        SecurityTone.Good => ThemeBrush("SystemFillColorSuccessBackgroundBrush"),
        _ => ThemeBrush("SubtleFillColorSecondaryBrush"),
    };

    public Brush SecurityForeground => Tone switch
    {
        SecurityTone.Bad => ThemeBrush("SystemFillColorCriticalBrush"),
        SecurityTone.Good => ThemeBrush("SystemFillColorSuccessBrush"),
        _ => ThemeBrush("TextFillColorSecondaryBrush"),
    };

    private void RaiseSecurityChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(IsSecurityChipVisible), nameof(IsSecurityBusy), nameof(SecurityText), nameof(SecurityTooltip),
                     nameof(SecurityGlyph), nameof(SecurityBackground), nameof(SecurityForeground),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private static Brush ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var b) && b is Brush brush ? brush : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    private enum SecurityTone
    {
        Neutral,
        Good,
        Bad,
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
