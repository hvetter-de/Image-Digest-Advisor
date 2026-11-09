using ImageDigestAdvisor.Core.References;

namespace ImageDigestAdvisor.Core.Analysis;

/// <summary>Everything we know about one image (for one platform).</summary>
public sealed record ImageDetails
{
    public required ImageReference Reference { get; init; }

    /// <summary>Digest of the platform-specific manifest.</summary>
    public required string ManifestDigest { get; init; }

    /// <summary>Digest of the multi-arch index (manifest list), if the image has one.</summary>
    public string? IndexDigest { get; init; }

    public required Platform Platform { get; init; }

    public IReadOnlyList<Platform> AvailablePlatforms { get; init; } = [];

    public DateTimeOffset? Created { get; init; }

    /// <summary>Compressed size (layers + config) as stored in the registry.</summary>
    public long CompressedSize { get; init; }

    public int LayerCount { get; init; }

    public string? MediaType { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>Tags known to point at this image (may be incomplete for registries without a tag API).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Versions found in labels/environment (e.g. <c>NGINX_VERSION</c>), used to guess a missing tag.</summary>
    public IReadOnlyList<string> VersionHints { get; init; } = [];

    /// <summary>The digest users should pin: the index digest if one exists, otherwise the manifest digest.</summary>
    public string PinnableDigest => IndexDigest ?? ManifestDigest;

    public bool HasDigest(string digest) =>
        string.Equals(ManifestDigest, digest, StringComparison.OrdinalIgnoreCase)
        || string.Equals(IndexDigest, digest, StringComparison.OrdinalIgnoreCase);

    public TimeSpan? Age(DateTimeOffset now) => Created is { } c ? now - c : null;

    public string? Label(string key) => Labels.TryGetValue(key, out var v) ? v : null;
}

public enum SuggestionKind
{
    /// <summary>Where the same tag points today.</summary>
    SameTag,
    NewestPatch,
    NewestMinor,
    NewestMajor,
}

public sealed record Suggestion
{
    public required SuggestionKind Kind { get; init; }

    /// <summary>Tag the suggestion is based on.</summary>
    public required string Tag { get; init; }

    public required ImageDetails Details { get; init; }

    /// <summary>Ready-to-paste reference, <c>repo:tag@sha256:…</c>.</summary>
    public string PinnedReference => Details.Reference.WithTag(Tag).WithDigest(Details.PinnableDigest).ToString();

    /// <summary>Other kinds that resolved to the same image (e.g. same tag is also the newest patch).</summary>
    public IReadOnlyList<SuggestionKind> AlsoKinds { get; init; } = [];

    public string Title => Kind switch
    {
        SuggestionKind.SameTag => $"Current digest of '{Tag}'",
        SuggestionKind.NewestPatch => $"Newest patch: {Tag}",
        SuggestionKind.NewestMinor => $"Newest minor: {Tag}",
        SuggestionKind.NewestMajor => $"Newest major: {Tag}",
        _ => Tag,
    };
}

public enum FindingKind
{
    UpToDate,
    TagMoved,
    NewerPatchAvailable,
    NewerMinorAvailable,
    NewerMajorAvailable,
    OlderThanThreshold,
    DigestNoLongerTagged,
    TagUnknown,
}

public enum Severity
{
    Info,
    Ok,
    Warning,
}

public sealed record Finding(FindingKind Kind, Severity Severity, string Message);

public enum OverallStatus
{
    UpToDate,
    Outdated,
    Unknown,
}

public sealed record RateLimitInfo(int? Limit, int? Remaining, int? WindowSeconds, bool Authenticated);

public sealed record AnalysisOptions
{
    public Platform Platform { get; init; } = Platform.LinuxAmd64;

    /// <summary>Images older than this are flagged. Null disables the check.</summary>
    public int? MaxAgeDays { get; init; } = 90;
}

public sealed record AnalysisResult
{
    public required ImageReference Input { get; init; }

    public required ImageDetails Requested { get; init; }

    /// <summary>The tag used for comparison (given by the user or found via reverse lookup).</summary>
    public string? EffectiveTag { get; init; }

    public bool TagWasInferred { get; init; }

    public required OverallStatus Status { get; init; }

    public IReadOnlyList<Finding> Findings { get; init; } = [];

    public IReadOnlyList<Suggestion> Suggestions { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public RateLimitInfo? RateLimit { get; init; }

    public DateTimeOffset AnalyzedAt { get; init; }
}
