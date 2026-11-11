using System.Text.Json;
using ImageDigestAdvisor.Core.Analysis;
using ImageDigestAdvisor.Core.Registry;

namespace ImageDigestAdvisor.Core.Security;

/// <summary>A software bill of materials found for an image: where it came from and the packages it lists.</summary>
public sealed record Sbom(string Source, IReadOnlyList<PackageUrl> Packages);

/// <summary>
/// Finds the SBOM an image publishes in its registry. Nothing is downloaded from the image layers.
/// <list type="number">
/// <item>BuildKit / Docker Hub attestations: an <c>attestation-manifest</c> entry in the image index whose
/// in-toto layer has the predicate type <c>https://spdx.dev/Document</c>.</item>
/// <item>OCI 1.1 referrers with artifact type <c>application/spdx+json</c> (e.g. pushed with ORAS).</item>
/// </list>
/// </summary>
public sealed class SbomReader(RegistryClient registry)
{
    public const string SpdxArtifactType = "application/spdx+json";

    /// <summary>
    /// Reading an attestation can cost up to two Docker Hub pulls the first time (index and attestation
    /// manifest; both are cached forever). Below this many remaining pulls the check is skipped.
    /// </summary>
    public static int MinDockerHubPullsLeft { get; set; } = 5;

    public async Task<Sbom?> GetAsync(ImageDetails image, CancellationToken ct = default)
    {
        var repo = image.Reference.WithTag(null).WithDigest(null);

        if (repo.IsDockerHub && registry.DockerHubRateLimit is { Remaining: { } remaining } && remaining < MinDockerHubPullsLeft)
        {
            throw new RegistryException(RegistryErrorKind.RateLimited,
                $"Skipped to save your Docker Hub rate limit ({remaining} pulls left). Add a Docker Hub account under Registry accounts to raise it.");
        }

        if (image.IndexDigest is { } indexDigest)
        {
            var index = await registry.GetManifestAsync(repo, indexDigest, ct).ConfigureAwait(false);
            if (index.IsIndex && FindAttestationManifest(index.Content, image.ManifestDigest) is { } attestationDigest)
            {
                var attestation = await registry.GetManifestAsync(repo, attestationDigest, ct).ConfigureAwait(false);
                if (FindSpdxLayer(attestation.Content) is { } layer)
                {
                    var blob = await registry.GetBlobAsync(repo, layer, ct).ConfigureAwait(false);
                    return new Sbom("SBOM attestation in the image index (SPDX)", ParseSpdx(blob));
                }
            }
        }

        foreach (var subject in new[] { image.ManifestDigest, image.IndexDigest }.OfType<string>())
        {
            byte[]? referrers;
            try
            {
                referrers = await registry.GetReferrersAsync(repo, subject, SpdxArtifactType, ct).ConfigureAwait(false);
            }
            catch (RegistryException)
            {
                referrers = null;
            }
            if (referrers is null) break; // registry doesn't support the Referrers API

            foreach (var artifactDigest in SpdxReferrers(referrers))
            {
                var artifact = await registry.GetManifestAsync(repo, artifactDigest, ct).ConfigureAwait(false);
                if (FirstLayer(artifact.Content) is { } layer)
                {
                    var blob = await registry.GetBlobAsync(repo, layer, ct).ConfigureAwait(false);
                    return new Sbom("SBOM attached as OCI referrer (SPDX)", ParseSpdx(blob));
                }
            }
        }

        return null;
    }

    /// <summary>The attestation manifest that BuildKit links to <paramref name="manifestDigest"/> in the index.</summary>
    internal static string? FindAttestationManifest(byte[] index, string manifestDigest)
    {
        using var doc = JsonDocument.Parse(index);
        if (!doc.RootElement.TryGetProperty("manifests", out var manifests)) return null;

        foreach (var m in manifests.EnumerateArray())
        {
            if (!m.TryGetProperty("annotations", out var a)) continue;
            if (Str(a, "vnd.docker.reference.type") == "attestation-manifest"
                && string.Equals(Str(a, "vnd.docker.reference.digest"), manifestDigest, StringComparison.OrdinalIgnoreCase))
            {
                return Str(m, "digest");
            }
        }
        return null;
    }

    internal static string? FindSpdxLayer(byte[] manifest)
    {
        using var doc = JsonDocument.Parse(manifest);
        if (!doc.RootElement.TryGetProperty("layers", out var layers)) return null;

        foreach (var layer in layers.EnumerateArray())
        {
            if (layer.TryGetProperty("annotations", out var a)
                && Str(a, "in-toto.io/predicate-type") is { } predicate
                && predicate.StartsWith("https://spdx.dev/Document", StringComparison.Ordinal))
            {
                return Str(layer, "digest");
            }
        }
        return null;
    }

    private static IEnumerable<string> SpdxReferrers(byte[] referrers)
    {
        using var doc = JsonDocument.Parse(referrers);
        var result = new List<string>();
        foreach (var m in doc.RootElement.GetProperty("manifests").EnumerateArray())
        {
            if (Str(m, "artifactType") == SpdxArtifactType && Str(m, "digest") is { } d) result.Add(d);
        }
        return result;
    }

    private static string? FirstLayer(byte[] manifest)
    {
        using var doc = JsonDocument.Parse(manifest);
        return doc.RootElement.TryGetProperty("layers", out var layers) && layers.GetArrayLength() > 0
            ? Str(layers[0], "digest")
            : null;
    }

    /// <summary>
    /// Reads the package URLs from an SPDX JSON document. Accepts a plain SPDX document, an in-toto
    /// statement (<c>predicate</c>) and a DSSE envelope (<c>payload</c>, base64).
    /// </summary>
    internal static IReadOnlyList<PackageUrl> ParseSpdx(byte[] content)
    {
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.String)
        {
            return ParseSpdx(Convert.FromBase64String(payload.GetString()!));
        }

        var spdx = root.TryGetProperty("predicate", out var predicate) ? predicate : root;
        var result = new List<PackageUrl>();
        if (!spdx.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array) return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in packages.EnumerateArray())
        {
            if (!package.TryGetProperty("externalRefs", out var refs) || refs.ValueKind != JsonValueKind.Array) continue;
            foreach (var r in refs.EnumerateArray())
            {
                if (Str(r, "referenceType") != "purl") continue;
                var locator = Str(r, "referenceLocator");
                if (locator is not null && seen.Add(locator) && PackageUrl.TryParse(locator, out var purl)) result.Add(purl);
            }
        }
        return result;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
