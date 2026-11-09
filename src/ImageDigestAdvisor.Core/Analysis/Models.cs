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

    /// <summary>The image config (environment, entrypoint, user, history, …), if the artifact has one.</summary>
    public ImageDigestAdvisor.Core.Registry.ImageConfig? Config { get; init; }

    /// <summary>
    /// Software versions the image declares in its environment, e.g. <c>DOTNET_VERSION=10.0.14</c>,
    /// <c>NGINX_VERSION=1.27.2</c>, <c>PYTHON_VERSION=3.13.1</c>. Order as in the image.
    /// </summary>
    public IReadOnlyList<SoftwareVersion> SoftwareVersions =>
        Config is null
            ? []
            : Config.Environment
                .Where(e => e.Key.EndsWith("_VERSION", StringComparison.Ordinal) && LooksLikeVersion(e.Value))
                .Select(e => new SoftwareVersion(e.Key, e.Value.Trim()))
                .ToList();

    /// <summary>True if any declared software version is a preview / release candidate.</summary>
    public bool ContainsPrereleaseSoftware => SoftwareVersions.Any(v => v.IsPrerelease);

    /// <summary>Differences in <see cref="SoftwareVersions"/> between <paramref name="older"/> and this image.</summary>
    public IReadOnlyList<SoftwareVersionChange> SoftwareChangesSince(ImageDetails older)
    {
        var before = older.SoftwareVersions.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        var after = SoftwareVersions.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        var changes = new List<SoftwareVersionChange>();

        foreach (var v in SoftwareVersions)
        {
            before.TryGetValue(v.Name, out var old);
            if (old != v.Value) changes.Add(new SoftwareVersionChange(v.Name, old, v.Value));
        }
        foreach (var v in older.SoftwareVersions.Where(v => !after.ContainsKey(v.Name)))
        {
            changes.Add(new SoftwareVersionChange(v.Name, v.Value, null));
        }
        return changes;
    }

    private static bool LooksLikeVersion(string value)
    {
        var v = value.Trim().TrimStart('v', 'V');
        return v.Length > 0 && char.IsDigit(v[0]) && !v.Contains(' ');
    }

    /// <summary>The digest users should pin: the index digest if one exists, otherwise the manifest digest.</summary>
    public string PinnableDigest => IndexDigest ?? ManifestDigest;

    public bool HasDigest(string digest) =>
        string.Equals(ManifestDigest, digest, StringComparison.OrdinalIgnoreCase)
        || string.Equals(IndexDigest, digest, StringComparison.OrdinalIgnoreCase);

    public TimeSpan? Age(DateTimeOffset now) => Created is { } c ? now - c : null;

    public string? Label(string key) => Labels.TryGetValue(key, out var v) ? v : null;
}

/// <summary>A software version declared by the image, e.g. <c>DOTNET_VERSION</c> = <c>10.0.14</c>.</summary>
public sealed record SoftwareVersion(string Name, string Value)
{
    /// <summary>True for preview/release-candidate versions such as <c>11.0.0-rc.3.26522.148</c> or <c>9.0.0-preview.7</c>.</summary>
    public bool IsPrerelease =>
        Value.Contains('-') && PrereleaseMarkers.Any(m => Value.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] PrereleaseMarkers = ["-rc", "-preview", "-alpha", "-beta", "-pre", "-dev", "-nightly"];

    /// <summary><c>DOTNET_SDK_VERSION</c> → <c>.NET SDK</c>, <c>NGINX_VERSION</c> → <c>nginx</c>, otherwise the variable name.</summary>
    public string DisplayName => KnownNames.TryGetValue(Name, out var n) ? n : Name;

    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.Ordinal)
    {
        ["DOTNET_VERSION"] = ".NET Runtime",
        ["ASPNET_VERSION"] = "ASP.NET Core",
        ["ASPNETCORE_VERSION"] = "ASP.NET Core",
        ["DOTNET_SDK_VERSION"] = ".NET SDK",
        ["POWERSHELL_VERSION"] = "PowerShell",
        ["NGINX_VERSION"] = "nginx",
        ["NJS_VERSION"] = "njs (nginx JavaScript)",
        ["NODE_VERSION"] = "Node.js",
        ["YARN_VERSION"] = "Yarn",
        ["PYTHON_VERSION"] = "Python",
        ["PYTHON_PIP_VERSION"] = "pip",
        ["JAVA_VERSION"] = "Java",
        ["GOLANG_VERSION"] = "Go",
        ["RUBY_VERSION"] = "Ruby",
        ["PHP_VERSION"] = "PHP",
        ["REDIS_VERSION"] = "Redis",
        ["PG_VERSION"] = "PostgreSQL",
        ["PG_MAJOR"] = "PostgreSQL major",
        ["MYSQL_VERSION"] = "MySQL",
        ["MARIADB_VERSION"] = "MariaDB",
        ["MONGO_VERSION"] = "MongoDB",
        ["HTTPD_VERSION"] = "Apache httpd",
        ["TOMCAT_VERSION"] = "Tomcat",
        ["MAVEN_VERSION"] = "Maven",
        ["GRADLE_VERSION"] = "Gradle",
        ["RUST_VERSION"] = "Rust",
        ["ALPINE_VERSION"] = "Alpine",
    };
}

/// <summary>A software version that differs between two images. Old/New is null if added/removed.</summary>
public sealed record SoftwareVersionChange(string Name, string? Old, string? New)
{
    public string DisplayName => new SoftwareVersion(Name, string.Empty).DisplayName;

    public override string ToString() => (Old, New) switch
    {
        (null, _) => $"{DisplayName} {New} (new)",
        (_, null) => $"{DisplayName} {Old} (removed)",
        _ => $"{DisplayName} {Old} → {New}",
    };
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
    SoftwareChanges,
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
