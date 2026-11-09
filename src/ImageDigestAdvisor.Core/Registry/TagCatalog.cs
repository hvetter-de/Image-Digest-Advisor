using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ImageDigestAdvisor.Core.References;

namespace ImageDigestAdvisor.Core.Registry;

/// <summary>A tag with whatever metadata the source provides (Docker Hub provides digests and dates).</summary>
public sealed record TagInfo(string Name)
{
    /// <summary>Digest the tag points to (index digest for multi-arch images), if known.</summary>
    public string? Digest { get; init; }

    public DateTimeOffset? LastPushed { get; init; }

    /// <summary>Per-platform manifest digests, if known.</summary>
    public IReadOnlyList<(Platform Platform, string Digest)> PlatformDigests { get; init; } = [];

    public bool Contains(string digest) =>
        string.Equals(Digest, digest, StringComparison.OrdinalIgnoreCase)
        || PlatformDigests.Any(p => string.Equals(p.Digest, digest, StringComparison.OrdinalIgnoreCase));
}

public sealed record TagList(IReadOnlyList<TagInfo> Tags, bool Complete, bool HasDigests, string Source);

public interface ITagCatalog
{
    Task<TagList> GetTagsAsync(ImageReference repo, CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses the Docker Hub web API for Docker Hub repositories (tag → digest in a few calls, no pull
/// limit consumption) and the registry's <c>tags/list</c> endpoint for every other registry.
/// Results are cached in memory for a few minutes because tags move.
/// </summary>
public sealed class TagCatalog(HttpClient http, RegistryClient registry) : ITagCatalog
{
    public static TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Docker Hub pages hold 100 tags, sorted by last update. Hub refuses anonymous paging beyond
    /// 1000 results (HTTP 403), so older tags are only reachable through direct lookups.
    /// </summary>
    public int MaxDockerHubPages { get; init; } = 10;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, TagList List)> _cache = new();

    public async Task<TagList> GetTagsAsync(ImageReference repo, CancellationToken cancellationToken = default)
    {
        var key = $"{repo.Registry}/{repo.Repository}";
        if (_cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheDuration) return hit.List;

        TagList list;
        if (repo.IsDockerHub)
        {
            try
            {
                list = await GetDockerHubTagsAsync(repo, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or RegistryException && !cancellationToken.IsCancellationRequested)
            {
                list = await GetRegistryTagsAsync(repo, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            list = await GetRegistryTagsAsync(repo, cancellationToken).ConfigureAwait(false);
        }

        _cache[key] = (DateTimeOffset.UtcNow, list);
        return list;
    }

    private async Task<TagList> GetRegistryTagsAsync(ImageReference repo, CancellationToken ct)
    {
        var (tags, complete) = await registry.ListTagsAsync(repo, ct: ct).ConfigureAwait(false);
        return new TagList(tags.Select(t => new TagInfo(t)).ToList(), complete, HasDigests: false, Source: "registry");
    }

    private async Task<TagList> GetDockerHubTagsAsync(ImageReference repo, CancellationToken ct)
    {
        var slash = repo.Repository.IndexOf('/');
        var ns = repo.Repository[..slash];
        var name = repo.Repository[(slash + 1)..];

        var tags = new List<TagInfo>();
        string? url = $"https://hub.docker.com/v2/namespaces/{ns}/repositories/{name}/tags?page_size=100&ordering=last_updated";
        var pages = 0;

        while (url is not null && pages < MaxDockerHubPages)
        {
            pages++;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("ImageDigestAdvisor/1.0");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode && tags.Count > 0)
            {
                // Paging limit reached: keep what we have (the most recently updated tags).
                break;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Could be a private repository the web API cannot see anonymously.
                throw new RegistryException(RegistryErrorKind.NotFound, "Repository not found on Docker Hub API.", response.StatusCode);
            }
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (root.TryGetProperty("results", out var results))
            {
                foreach (var r in results.EnumerateArray())
                {
                    tags.Add(ParseHubTag(r));
                }
            }

            url = root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }

        return new TagList(tags, Complete: url is null, HasDigests: true, Source: "Docker Hub API");
    }

    internal static TagInfo ParseHubTag(JsonElement r)
    {
        var name = r.GetProperty("name").GetString()!;
        var digest = r.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

        DateTimeOffset? pushed = null;
        foreach (var field in new[] { "tag_last_pushed", "last_updated" })
        {
            if (r.TryGetProperty(field, out var p) && p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), out var parsed))
            {
                pushed = parsed;
                break;
            }
        }

        var platforms = new List<(Platform, string)>();
        if (r.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var img in images.EnumerateArray())
            {
                var os = Str(img, "os");
                var arch = Str(img, "architecture");
                var imgDigest = Str(img, "digest");
                if (os is null || arch is null || imgDigest is null || os == "unknown") continue;
                platforms.Add((new Platform(os, arch, string.IsNullOrEmpty(Str(img, "variant")) ? null : Str(img, "variant")), imgDigest));
            }
        }

        // Single-platform images: the Hub "digest" is the manifest digest itself.
        return new TagInfo(name) { Digest = digest, LastPushed = pushed, PlatformDigests = platforms };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
