using System.Globalization;
using System.Text.Json;
using ImageDigestAdvisor.Core.References;

namespace ImageDigestAdvisor.Core.Registry;

public static class MediaTypes
{
    public const string OciIndex = "application/vnd.oci.image.index.v1+json";
    public const string OciManifest = "application/vnd.oci.image.manifest.v1+json";
    public const string DockerManifestList = "application/vnd.docker.distribution.manifest.list.v2+json";
    public const string DockerManifest = "application/vnd.docker.distribution.manifest.v2+json";

    public static readonly string[] AcceptAll = [OciIndex, OciManifest, DockerManifestList, DockerManifest];
}

/// <summary>A manifest or index as returned by the registry.</summary>
public sealed record ManifestDocument(string Digest, string? MediaType, byte[] Content)
{
    public bool IsIndex
    {
        get
        {
            if (MediaType is MediaTypes.OciIndex or MediaTypes.DockerManifestList) return true;
            if (MediaType is MediaTypes.OciManifest or MediaTypes.DockerManifest) return false;
            using var doc = JsonDocument.Parse(Content);
            return doc.RootElement.TryGetProperty("manifests", out _);
        }
    }
}

public sealed record IndexEntry(string Digest, Platform Platform, long Size);

public sealed record ImageManifest(string ConfigDigest, string? ConfigMediaType, long ConfigSize, IReadOnlyList<long> LayerSizes);

public sealed record ImageConfig(
    DateTimeOffset? Created,
    Platform? Platform,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> Environment);

public static class ManifestParser
{
    public static string? DetectMediaType(byte[] content)
    {
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.TryGetProperty("mediaType", out var mt) && mt.ValueKind == JsonValueKind.String ? mt.GetString() : null;
    }

    /// <summary>Parses an index and drops attestation entries (<c>unknown/unknown</c>).</summary>
    public static IReadOnlyList<IndexEntry> ParseIndex(byte[] content)
    {
        using var doc = JsonDocument.Parse(content);
        var result = new List<IndexEntry>();
        if (!doc.RootElement.TryGetProperty("manifests", out var manifests)) return result;

        foreach (var m in manifests.EnumerateArray())
        {
            var digest = m.GetProperty("digest").GetString()!;
            var size = m.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            if (!m.TryGetProperty("platform", out var p)) continue;

            var os = Str(p, "os") ?? "unknown";
            var arch = Str(p, "architecture") ?? "unknown";
            if (os == "unknown" || arch == "unknown") continue;

            var variant = Str(p, "variant");
            var osVersion = Str(p, "os.version");
            // Windows images list several OS builds for the same os/arch; keep the build as variant so they are distinguishable.
            if (os == "windows" && variant is null && osVersion is not null) variant = osVersion;

            result.Add(new IndexEntry(digest, new Platform(os, arch, variant), size));
        }
        return result;
    }

    public static ImageManifest ParseManifest(byte[] content)
    {
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        if (root.TryGetProperty("schemaVersion", out var sv) && sv.GetInt32() == 1)
        {
            throw new RegistryException(RegistryErrorKind.Unsupported, "This image uses the deprecated Docker schema 1 manifest format, which is not supported.");
        }

        if (!root.TryGetProperty("config", out var config))
        {
            throw new RegistryException(RegistryErrorKind.InvalidResponse, "The manifest has no config section.");
        }

        var layers = root.TryGetProperty("layers", out var l)
            ? l.EnumerateArray().Select(x => x.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0).ToList()
            : [];

        return new ImageManifest(
            config.GetProperty("digest").GetString()!,
            Str(config, "mediaType"),
            config.TryGetProperty("size", out var cs) && cs.TryGetInt64(out var csn) ? csn : 0,
            layers);
    }

    public static ImageConfig ParseConfig(byte[] content)
    {
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        DateTimeOffset? created = null;
        if (Str(root, "created") is { } c
            && DateTimeOffset.TryParse(c, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            && parsed.Year > 1970) // reproducible builds often use the epoch
        {
            created = parsed;
        }

        Platform? platform = null;
        if (Str(root, "os") is { } os && Str(root, "architecture") is { } arch)
        {
            platform = new Platform(os, arch, Str(root, "variant"));
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
        {
            if (cfg.TryGetProperty("Labels", out var lbl) && lbl.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in lbl.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String) labels[p.Name] = p.Value.GetString()!;
                }
            }

            if (cfg.TryGetProperty("Env", out var e) && e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray())
                {
                    var text = item.GetString();
                    var eq = text?.IndexOf('=') ?? -1;
                    if (eq > 0) env[text![..eq]] = text[(eq + 1)..];
                }
            }
        }

        return new ImageConfig(created, platform, labels, env);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
