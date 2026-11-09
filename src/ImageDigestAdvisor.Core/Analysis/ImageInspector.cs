using ImageDigestAdvisor.Core.References;
using ImageDigestAdvisor.Core.Registry;

namespace ImageDigestAdvisor.Core.Analysis;

/// <summary>Loads <see cref="ImageDetails"/> (created date, size, labels, …) for a tag or digest.</summary>
public sealed class ImageInspector(RegistryClient registry)
{
    /// <param name="repo">Repository (tag/digest on it are ignored).</param>
    /// <param name="reference">Tag or digest.</param>
    /// <param name="platform">Platform to pick if the reference is a multi-arch index.</param>
    /// <param name="hint">Docker Hub tag metadata; lets us skip downloading the index.</param>
    public async Task<ImageDetails> InspectAsync(ImageReference repo, string reference, Platform platform, TagInfo? hint = null, CancellationToken ct = default)
    {
        repo = repo.WithTag(null).WithDigest(null);

        // Fast path: Docker Hub already told us which platform manifest belongs to this tag.
        if (hint is { Digest: { } hintIndex } && hint.PlatformDigests.Count > 1 && Select(hint.PlatformDigests.Select(p => p.Platform).ToList(), platform) is { } hinted)
        {
            var platformDigest = hint.PlatformDigests.First(p => p.Platform == hinted).Digest;
            var details = await InspectManifestAsync(repo, platformDigest, ct).ConfigureAwait(false);
            return details with
            {
                IndexDigest = hintIndex,
                AvailablePlatforms = hint.PlatformDigests.Select(p => p.Platform).Distinct().ToList(),
            };
        }

        var top = await registry.GetManifestAsync(repo, reference, ct).ConfigureAwait(false);
        if (!top.IsIndex)
        {
            var single = await InspectManifestAsync(repo, top.Digest, ct, top).ConfigureAwait(false);
            return single with { AvailablePlatforms = [single.Platform] };
        }

        var entries = ManifestParser.ParseIndex(top.Content);
        if (entries.Count == 0)
        {
            throw new RegistryException(RegistryErrorKind.Unsupported, "The image index contains no runnable platforms.");
        }

        var available = entries.Select(e => e.Platform).Distinct().ToList();
        var chosen = Select(available, platform) ?? available[0];
        var entry = entries.First(e => e.Platform == chosen);

        var result = await InspectManifestAsync(repo, entry.Digest, ct).ConfigureAwait(false);
        return result with { IndexDigest = top.Digest, AvailablePlatforms = available };
    }

    /// <summary>Exact platform match first, then os/arch ignoring variant (e.g. Windows OS builds).</summary>
    internal static Platform? Select(IReadOnlyList<Platform> available, Platform wanted) =>
        available.FirstOrDefault(p => p.Matches(wanted))
        ?? available.FirstOrDefault(p => string.Equals(p.Os, wanted.Os, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(p.Architecture, wanted.Architecture, StringComparison.OrdinalIgnoreCase));

    private async Task<ImageDetails> InspectManifestAsync(ImageReference repo, string digest, CancellationToken ct, ManifestDocument? already = null)
    {
        var doc = already ?? await registry.GetManifestAsync(repo, digest, ct).ConfigureAwait(false);
        var manifest = ManifestParser.ParseManifest(doc.Content);

        ImageConfig? config = null;
        if (manifest.ConfigMediaType is null or "application/vnd.oci.image.config.v1+json" or "application/vnd.docker.container.image.v1+json")
        {
            var configBytes = await registry.GetBlobAsync(repo, manifest.ConfigDigest, ct).ConfigureAwait(false);
            config = ManifestParser.ParseConfig(configBytes);
        }

        return new ImageDetails
        {
            Reference = repo.WithDigest(doc.Digest),
            ManifestDigest = doc.Digest,
            Platform = config?.Platform ?? new Platform("unknown", "unknown"),
            Created = config?.Created,
            CompressedSize = manifest.LayerSizes.Sum() + manifest.ConfigSize,
            LayerCount = manifest.LayerSizes.Count,
            MediaType = doc.MediaType,
            Labels = config?.Labels ?? new Dictionary<string, string>(),
            VersionHints = config is null ? [] : VersionHintsFrom(config),
        };
    }

    /// <summary>
    /// Versions an image announces about itself, used to guess the tag of an untagged digest:
    /// the OCI version label and environment variables like <c>NGINX_VERSION=1.27.2</c>.
    /// </summary>
    internal static IReadOnlyList<string> VersionHintsFrom(ImageConfig config)
    {
        var hints = new List<string>();
        if (config.Labels.TryGetValue("org.opencontainers.image.version", out var v) && !string.IsNullOrWhiteSpace(v)) hints.Add(v.Trim());
        foreach (var (key, value) in config.Environment)
        {
            var trimmed = value.Trim();
            if (key.EndsWith("_VERSION", StringComparison.Ordinal) && trimmed.TrimStart('v') is { Length: > 0 } number && char.IsDigit(number[0]))
            {
                hints.Add(trimmed);
            }
        }
        return hints.Distinct().ToList();
    }
}
