using CommunityToolkit.Mvvm.ComponentModel;
using ImageDigestAdvisor.Core.Analysis;
using ImageDigestAdvisor.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ImageDigestAdvisor.App.ViewModels;

public sealed record FindingRow(string Glyph, Brush Brush, string Text);

public sealed partial class ResultViewModel : ObservableObject
{
    /// <summary>One line under the findings, e.g. "Your image has 3 vulnerabilities with a fix. The recommended update fixes 3."</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecuritySummary))]
    public partial string? SecuritySummary { get; set; }

    public bool HasSecuritySummary => SecuritySummary is not null;

    private IEnumerable<ImageCardViewModel> AllCards => Suggestions.Prepend(Requested);

    /// <summary>The check is off: let the user's card offer it.</summary>
    public void OfferVulnerabilityCheck()
    {
        foreach (var card in AllCards) card.ShowSecurityOffer();
    }

    /// <summary>Checks all cards for known vulnerabilities; cards update as their results arrive.</summary>
    public async Task ScanVulnerabilitiesAsync(VulnerabilityScanner scanner, CancellationToken ct)
    {
        SecuritySummary = null;
        foreach (var card in AllCards) card.BeginSecurityScan();

        var requestedTask = Task.Run(() => scanner.ScanAsync(Requested.Details, ct), ct);
        var suggestionTasks = Suggestions.Select(s => (Card: s, Task: Task.Run(() => scanner.ScanAsync(s.Details, ct), ct))).ToList();

        try
        {
            var baseline = await requestedTask;
            Requested.SetSecurityReport(baseline, null);
            foreach (var (card, task) in suggestionTasks)
            {
                card.SetSecurityReport(await task, baseline);
            }
            UpdateSecuritySummary(baseline);
        }
        catch (OperationCanceledException)
        {
            foreach (var card in AllCards) card.CancelSecurityScan();
        }
    }

    private void UpdateSecuritySummary(VulnerabilityReport baseline)
    {
        if (!baseline.IsCompleted || baseline.FixableCount == 0)
        {
            SecuritySummary = null;
            return;
        }

        var fixable = baseline.FixableCount;
        var text = fixable == 1
            ? "Your image has 1 known vulnerability with a fix available."
            : $"Your image has {fixable} known vulnerabilities with a fix available.";

        static int FixesFixable(ImageCardViewModel s) => s.FixedVulnerabilities.Count(f => f.Category == VulnerabilityCategory.Fixable);

        var best = Suggestions.FirstOrDefault(s => s.IsRecommended) is { } recommended && FixesFixable(recommended) > 0
            ? recommended
            : Suggestions.OrderByDescending(FixesFixable).FirstOrDefault(s => FixesFixable(s) > 0);
        if (best is not null)
        {
            var name = best.IsRecommended ? "The recommended update" : $"'{best.Title}'";
            var count = FixesFixable(best);
            var total = best.FixedVulnerabilities.Count;
            text += count == fixable ? $" {name} fixes all of them" : $" {name} fixes {count} of them";
            // A newer base OS can also remove vulnerabilities that had no fix in the old one.
            text += total > count ? $", and {total - count} more without a fix in your image's packages." : ".";
        }
        else
        {
            // Typical right after a distribution security update: the packages are fixed, the image isn't rebuilt yet.
            text +=$" No newer image includes the {(fixable == 1 ? "fix" : "fixes")} yet. "
                    + $"{(fixable == 1 ? "It's" : "They're")} available as package updates from the distribution, so you can wait for the publisher to rebuild the image, "
                    + $"or upgrade the packages in your own Dockerfile{UpgradeHint(baseline)}.";
        }

        SecuritySummary = text;
    }

    /// <summary>E.g. " (RUN apk upgrade --no-cache)" when all fixable packages are Alpine packages.</summary>
    private static string UpgradeHint(VulnerabilityReport report)
    {
        var families = report.Findings
            .Where(f => f.Category == VulnerabilityCategory.Fixable)
            .Select(f => f.Ecosystem.Split(':')[0])
            .Distinct()
            .ToList();
        return families switch
        {
            ["Alpine"] => " (RUN apk upgrade --no-cache)",
            ["Debian"] or ["Ubuntu"] => " (RUN apt-get update && apt-get upgrade -y)",
            _ => string.Empty,
        };
    }

    public ResultViewModel(AnalysisResult result)
    {
        var now = result.AnalyzedAt;

        (StatusTitle, StatusSeverity) = result.Status switch
        {
            OverallStatus.UpToDate => ("Up to date", InfoBarSeverity.Success),
            OverallStatus.Outdated => ("Outdated", InfoBarSeverity.Warning),
            _ => ("Could not determine", InfoBarSeverity.Informational),
        };

        StatusMessage = result.Status switch
        {
            OverallStatus.Outdated when result.Suggestions.Count > 0 => "A newer image is available. See the suggested digest below.",
            OverallStatus.Outdated => "No newer image was found, but the image is old.",
            OverallStatus.UpToDate => "No newer image was found for this tag.",
            _ => "Enter the image with its tag (repo:tag@sha256:…) to compare it with newer images.",
        };

        Findings = result.Findings.Select(ToRow).ToList();
        Warnings = result.Warnings.ToList();

        var requestedTitle = result.Input.Digest is null ? "Current image" : "Your image";
        var requestedTag = result.EffectiveTag ?? result.Input.Tag;
        var requestedRef = result.Requested.Reference.WithTag(requestedTag).WithDigest(result.Requested.PinnableDigest).ToString();
        var requestedSubtitle = result switch
        {
            { TagWasInferred: true } => $"tag '{result.EffectiveTag}' guessed from the version inside the image",
            { Input.Tag: null, EffectiveTag: { } t } => $"tag '{t}' found for this digest",
            { Input.Tag: null } => "no tag points to this digest",
            { Input.Digest: null } => $"where '{result.Input.Tag}' points today",
            _ => "as entered",
        };
        Requested = new ImageCardViewModel(requestedTitle, requestedSubtitle, requestedRef, result.Requested, now);

        // Never recommend preview/RC software to someone running a stable release.
        var allowPrerelease = result.Requested.ContainsPrereleaseSoftware;
        var recommended = PickRecommended(result.Suggestions.Where(s => allowPrerelease || !s.Details.ContainsPrereleaseSoftware).ToList());
        Suggestions = result.Suggestions
            .Select(s => new ImageCardViewModel(s.Title, Describe(s, allowPrerelease), s.PinnedReference, s.Details, now, ReferenceEquals(s, recommended), compareTo: result.Requested))
            .ToList();

        if (result.RateLimit is { Remaining: { } remaining } rl)
        {
            var window = rl.WindowSeconds is { } w ? w >= 3600 ? $"{w / 3600} h" : $"{w / 60} min" : "window";
            RateLimitText = $"Docker Hub pulls left: {remaining}{(rl.Limit is { } l ? $" of {l}" : string.Empty)} per {window} ({(rl.Authenticated ? "signed in" : "anonymous")}). Repeated checks are served from the local cache.";
            ShowAddAccountHint = !rl.Authenticated;
        }
    }

    public string StatusTitle { get; }

    public string StatusMessage { get; }

    public InfoBarSeverity StatusSeverity { get; }

    public IReadOnlyList<FindingRow> Findings { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public ImageCardViewModel Requested { get; }

    public IReadOnlyList<ImageCardViewModel> Suggestions { get; }

    public bool HasSuggestions => Suggestions.Count > 0;

    public bool HasNoSuggestions => Suggestions.Count == 0;

    public string? RateLimitText { get; }

    public bool HasRateLimit => RateLimitText is not null;

    public bool ShowAddAccountHint { get; }

    /// <summary>
    /// The recommended update: newest patch of the same line, else the current digest of the same tag,
    /// else the newest minor. A new major is never recommended automatically.
    /// </summary>
    private static Suggestion? PickRecommended(IReadOnlyList<Suggestion> suggestions)
    {
        foreach (var kind in new[] { SuggestionKind.NewestPatch, SuggestionKind.SameTag, SuggestionKind.NewestMinor })
        {
            var match = suggestions.FirstOrDefault(s => s.Kind == kind || s.AlsoKinds.Contains(kind));
            if (match is not null) return match;
        }
        return null;
    }

    private static string Describe(Suggestion s, bool allowPrerelease)
    {
        var description = DescribeKinds(s);
        return s.Details.ContainsPrereleaseSoftware && !allowPrerelease
            ? description + " · ⚠ pre-release (preview/RC), not a stable version"
            : description;
    }

    private static string DescribeKinds(Suggestion s)
    {
        var kinds = new[] { s.Kind }.Concat(s.AlsoKinds).Select(k => k switch
        {
            SuggestionKind.SameTag => "same tag, rebuilt",
            SuggestionKind.NewestPatch => "newest patch",
            SuggestionKind.NewestMinor => "newest minor",
            SuggestionKind.NewestMajor => "newest major – check for breaking changes",
            _ => k.ToString(),
        });
        return string.Join(" · ", kinds);
    }

    private static FindingRow ToRow(Finding f)
    {
        var (glyph, key) = f.Severity switch
        {
            Severity.Ok => ("", "SystemFillColorSuccessBrush"),     // CheckMark
            Severity.Warning => ("", "SystemFillColorCautionBrush"), // Warning
            _ => ("", "SystemFillColorAttentionBrush"),             // Info
        };
        var brush = Application.Current.Resources.TryGetValue(key, out var b) && b is Brush br ? br : new SolidColorBrush(Microsoft.UI.Colors.Gray);
        return new FindingRow(glyph, brush, f.Message);
    }
}
