using ImageDigestAdvisor.Core.Analysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ImageDigestAdvisor.App.ViewModels;

public sealed record FindingRow(string Glyph, Brush Brush, string Text);

public sealed class ResultViewModel
{
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

        var recommended = PickRecommended(result.Suggestions);
        Suggestions = result.Suggestions
            .Select(s => new ImageCardViewModel(s.Title, Describe(s), s.PinnedReference, s.Details, now, ReferenceEquals(s, recommended)))
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

    private static string Describe(Suggestion s)
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
