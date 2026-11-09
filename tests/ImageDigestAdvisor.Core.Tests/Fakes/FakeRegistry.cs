using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ImageDigestAdvisor.Core.Registry;

namespace ImageDigestAdvisor.Core.Tests.Fakes;

/// <summary>
/// In-memory OCI registry (plus optional Docker Hub tag API) served through an <see cref="HttpMessageHandler"/>.
/// Requires a Bearer token obtained from <c>https://auth.example/token</c>, like real registries.
/// </summary>
internal sealed class FakeRegistry : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _content = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _mediaTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Repo, string Tag), string> _tags = [];
    private readonly Dictionary<string, string> _hubResponses = new(StringComparer.Ordinal);

    public List<(HttpMethod Method, string Url)> Requests { get; } = [];

    /// <summary>If set, the token endpoint only accepts these Basic credentials (anonymous is still allowed unless <see cref="RequireLogin"/>).</summary>
    public (string User, string Password)? ValidLogin { get; set; }

    public bool RequireLogin { get; set; }

    public string? RateLimitRemaining { get; set; }

    public int ManifestGets => Requests.Count(r => r.Method == HttpMethod.Get && r.Url.Contains("/manifests/", StringComparison.Ordinal));

    public void Tag(string repo, string tag, string digest) => _tags[(repo, tag)] = digest;

    public void Untag(string repo, string tag) => _tags.Remove((repo, tag));

    public void AddHubResponse(string url, string json) => _hubResponses[url] = json;

    /// <summary>Adds a multi-arch image (amd64 + arm64) and returns its index and per-platform digests.</summary>
    public FakeImage AddImage(DateTimeOffset created, IDictionary<string, string>? labels = null, IDictionary<string, string>? env = null, string salt = "")
    {
        var platforms = new[] { ("linux", "amd64", (string?)null), ("linux", "arm64", "v8") };
        var entries = new List<object>();
        var manifestDigests = new Dictionary<string, string>();

        foreach (var (os, arch, variant) in platforms)
        {
            var config = Json(new
            {
                created = created.ToString("O"),
                os,
                architecture = arch,
                variant,
                config = new
                {
                    Labels = labels ?? new Dictionary<string, string>(),
                    Env = (env ?? new Dictionary<string, string>()).Select(kv => $"{kv.Key}={kv.Value}").Append("SALT=" + salt).ToArray(),
                },
            });
            var configDigest = Store(config, "application/vnd.oci.image.config.v1+json");

            var layer = Encoding.UTF8.GetBytes($"layer-{arch}-{salt}-{created:O}");
            var layerDigest = Store(layer, "application/octet-stream");

            var manifest = Json(new
            {
                schemaVersion = 2,
                mediaType = MediaTypes.OciManifest,
                config = new { mediaType = "application/vnd.oci.image.config.v1+json", digest = configDigest, size = config.Length },
                layers = new[] { new { mediaType = "application/vnd.oci.image.layer.v1.tar+gzip", digest = layerDigest, size = 1000L } },
            });
            var manifestDigest = Store(manifest, MediaTypes.OciManifest);
            manifestDigests[$"{os}/{arch}"] = manifestDigest;
            entries.Add(new { mediaType = MediaTypes.OciManifest, digest = manifestDigest, size = manifest.Length, platform = new { os, architecture = arch, variant } });
        }

        // Attestation entry like BuildKit adds – must be ignored.
        entries.Add(new { mediaType = MediaTypes.OciManifest, digest = manifestDigests["linux/amd64"], size = 1, platform = new { os = "unknown", architecture = "unknown" } });

        var index = Json(new { schemaVersion = 2, mediaType = MediaTypes.OciIndex, manifests = entries });
        var indexDigest = Store(index, MediaTypes.OciIndex);
        return new FakeImage(indexDigest, manifestDigests["linux/amd64"], manifestDigests["linux/arm64"], created);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        lock (Requests) Requests.Add((request.Method, uri.ToString()));

        if (uri.Host == "hub.docker.com")
        {
            return Task.FromResult(_hubResponses.TryGetValue(uri.ToString(), out var hub)
                ? JsonResponse(hub)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        if (uri.Host == "auth.example")
        {
            var auth = request.Headers.Authorization;
            if (auth is not null)
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!)).Split(':', 2);
                if (ValidLogin is not { } login || decoded[0] != login.User || decoded[1] != login.Password)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }
                return Task.FromResult(JsonResponse("""{"token":"user-token","expires_in":300}"""));
            }
            return Task.FromResult(JsonResponse("""{"token":"anon-token","expires_in":300}"""));
        }

        var token = request.Headers.Authorization?.Parameter;
        var authorized = token == "user-token" || (token == "anon-token" && !RequireLogin);
        if (!authorized)
        {
            var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer",
                "realm=\"https://auth.example/token\",service=\"fake\",scope=\"repository:test:pull\""));
            return Task.FromResult(challenge);
        }

        var path = uri.AbsolutePath;
        if (path == "/v2/") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        var marker = path.LastIndexOf("/manifests/", StringComparison.Ordinal);
        if (marker > 0)
        {
            var repo = path[4..marker];
            var reference = path[(marker + "/manifests/".Length)..];
            var digest = reference.StartsWith("sha256:", StringComparison.Ordinal) ? reference : _tags.GetValueOrDefault((repo, reference));
            if (digest is null || !_content.TryGetValue(digest, out var body)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : body),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(_mediaTypes[digest]);
            response.Headers.Add("Docker-Content-Digest", digest);
            if (RateLimitRemaining is not null)
            {
                response.Headers.Add("ratelimit-limit", "100;w=21600");
                response.Headers.Add("ratelimit-remaining", RateLimitRemaining);
            }
            return Task.FromResult(response);
        }

        marker = path.LastIndexOf("/blobs/", StringComparison.Ordinal);
        if (marker > 0)
        {
            var digest = path[(marker + "/blobs/".Length)..];
            return Task.FromResult(_content.TryGetValue(digest, out var blob)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(blob) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        if (path.EndsWith("/tags/list", StringComparison.Ordinal))
        {
            var repo = path[4..^"/tags/list".Length];
            var names = _tags.Keys.Where(k => k.Repo == repo).Select(k => k.Tag).Order(StringComparer.Ordinal).ToArray();
            return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new { name = repo, tags = names })));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private string Store(byte[] content, string mediaType)
    {
        var digest = Digests.Sha256(content);
        _content[digest] = content;
        _mediaTypes[digest] = mediaType;
        return digest;
    }

    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed record FakeImage(string IndexDigest, string Amd64Digest, string Arm64Digest, DateTimeOffset Created);
