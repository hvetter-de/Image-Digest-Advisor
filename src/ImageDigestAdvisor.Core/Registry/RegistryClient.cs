using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImageDigestAdvisor.Core.Analysis;
using ImageDigestAdvisor.Core.Credentials;
using ImageDigestAdvisor.Core.References;

namespace ImageDigestAdvisor.Core.Registry;

/// <summary>
/// Minimal client for the OCI Distribution API (<c>/v2/…</c>). Handles the Bearer/Basic auth
/// challenge flow (anonymous or with credentials), content caching and Docker Hub rate-limit headers.
/// Talks HTTPS only; Docker does not need to be installed.
/// </summary>
public sealed partial class RegistryClient
{
    private const string UserAgent = "ImageDigestAdvisor/1.0";

    private readonly HttpClient _http;
    private readonly ICredentialProvider _credentials;
    private readonly IContentCache _cache;
    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new();
    private readonly ConcurrentDictionary<string, bool> _authenticatedRegistries = new();

    public RegistryClient(HttpClient http, ICredentialProvider credentials, IContentCache cache)
    {
        _http = http;
        _credentials = credentials;
        _cache = cache;
    }

    /// <summary>Last Docker Hub rate-limit information seen in a response.</summary>
    public RateLimitInfo? DockerHubRateLimit { get; private set; }

    /// <summary>Number of manifest GET requests sent (these count against Docker Hub's pull limit).</summary>
    public int ManifestGetCount => _manifestGets;

    private int _manifestGets;

    /// <summary>Forgets cached registry tokens, e.g. after an account was added or removed.</summary>
    public void ClearTokens()
    {
        _tokens.Clear();
        _authenticatedRegistries.Clear();
    }

    /// <summary>Resolves a tag (or digest) to the digest it points to, using a HEAD request where possible.</summary>
    public async Task<string> ResolveDigestAsync(ImageReference repo, string reference, CancellationToken ct = default)
    {
        if (Digests.IsDigest(reference)) return reference;

        using var response = await SendAsync(repo, () => ManifestRequest(HttpMethod.Head, repo, reference), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, repo, reference).ConfigureAwait(false);

        if (response.Headers.TryGetValues("Docker-Content-Digest", out var values) && values.FirstOrDefault() is { Length: > 0 } digest)
        {
            return digest;
        }

        // Registry without digest header: fall back to GET and hash the body.
        var manifest = await GetManifestAsync(repo, reference, ct).ConfigureAwait(false);
        return manifest.Digest;
    }

    /// <summary>Fetches a manifest. Tags are first resolved via HEAD so the GET can be served from the cache.</summary>
    public async Task<ManifestDocument> GetManifestAsync(ImageReference repo, string reference, CancellationToken ct = default)
    {
        if (!Digests.IsDigest(reference))
        {
            var digest = await ResolveDigestAsync(repo, reference, ct).ConfigureAwait(false);
            if (await _cache.GetAsync(digest, ct).ConfigureAwait(false) is { } cachedByTag)
            {
                return new ManifestDocument(digest, ManifestParser.DetectMediaType(cachedByTag), cachedByTag);
            }
            reference = digest;
        }

        if (await _cache.GetAsync(reference, ct).ConfigureAwait(false) is { } cached)
        {
            return new ManifestDocument(reference, ManifestParser.DetectMediaType(cached), cached);
        }

        Interlocked.Increment(ref _manifestGets);
        using var response = await SendAsync(repo, () => ManifestRequest(HttpMethod.Get, repo, reference), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, repo, reference).ConfigureAwait(false);

        var content = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var computed = Digests.Sha256(content);
        if (!string.Equals(computed, reference, StringComparison.OrdinalIgnoreCase))
        {
            throw new RegistryException(RegistryErrorKind.InvalidResponse, $"The registry returned content that does not match digest {reference}.");
        }

        await _cache.SetAsync(reference, content, ct).ConfigureAwait(false);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? ManifestParser.DetectMediaType(content);
        return new ManifestDocument(reference, mediaType, content);
    }

    public async Task<byte[]> GetBlobAsync(ImageReference repo, string digest, CancellationToken ct = default)
    {
        if (await _cache.GetAsync(digest, ct).ConfigureAwait(false) is { } cached) return cached;

        using var response = await SendAsync(repo, () => new HttpRequestMessage(HttpMethod.Get, $"https://{repo.ApiHost}/v2/{repo.Repository}/blobs/{digest}"), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, repo, digest).ConfigureAwait(false);

        var content = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (!Digests.Matches(digest, content))
        {
            throw new RegistryException(RegistryErrorKind.InvalidResponse, $"The registry returned a blob that does not match digest {digest}.");
        }

        await _cache.SetAsync(digest, content, ct).ConfigureAwait(false);
        return content;
    }

    /// <summary>
    /// Lists artifacts that refer to <paramref name="digest"/> (SBOMs, signatures) via the OCI 1.1 Referrers API.
    /// Returns null if the registry doesn't support it. The result is not cached: new referrers can appear at any time.
    /// </summary>
    public async Task<byte[]?> GetReferrersAsync(ImageReference repo, string digest, string? artifactType = null, CancellationToken ct = default)
    {
        var url = $"https://{repo.ApiHost}/v2/{repo.Repository}/referrers/{digest}"
                  + (artifactType is null ? string.Empty : "?artifactType=" + Uri.EscapeDataString(artifactType));
        using var response = await SendAsync(repo, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(MediaTypes.OciIndex);
            return request;
        }, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return null;
        var content = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.TryGetProperty("manifests", out _) ? content : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Lists tags via <c>/v2/&lt;repo&gt;/tags/list</c>, following pagination up to <paramref name="maxTags"/>.</summary>
    public async Task<(IReadOnlyList<string> Tags, bool Complete)> ListTagsAsync(ImageReference repo, int maxTags = 5000, CancellationToken ct = default)
    {
        var tags = new List<string>();
        string? url = $"https://{repo.ApiHost}/v2/{repo.Repository}/tags/list?n=1000";

        while (url is not null && tags.Count < maxTags)
        {
            var requestUrl = url;
            using var response = await SendAsync(repo, () => new HttpRequestMessage(HttpMethod.Get, requestUrl), ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, repo, "tags").ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
            {
                tags.AddRange(t.EnumerateArray().Select(x => x.GetString()).OfType<string>());
            }

            url = NextLink(response, requestUrl);
        }

        return (tags, url is null);
    }

    /// <summary>Verifies credentials by requesting a token for the registry.</summary>
    public async Task<CredentialTestResult> TestCredentialsAsync(string registry, string username, string secret, CancellationToken ct = default)
    {
        var normalized = RegistryNames.Normalize(registry);
        var host = normalized == ImageReference.DockerHubRegistry ? "registry-1.docker.io" : normalized;
        var credential = new RegistryCredential(normalized, username, secret, CredentialSource.App);
        var rejected = new CredentialTestResult(false, "The registry rejected the username or token.");

        try
        {
            using var ping = await _http.SendAsync(WithUserAgent(new HttpRequestMessage(HttpMethod.Get, $"https://{host}/v2/")), ct).ConfigureAwait(false);
            if (ping.IsSuccessStatusCode)
            {
                return new CredentialTestResult(true, "This registry does not require a login. The account is not needed.");
            }

            var challenge = ParseChallenge(ping);
            if (challenge is null) return new CredentialTestResult(false, $"Unexpected response from registry: {(int)ping.StatusCode} {ping.ReasonPhrase}");

            if (challenge.Scheme == "basic")
            {
                using var request = WithUserAgent(new HttpRequestMessage(HttpMethod.Get, $"https://{host}/v2/"));
                request.Headers.Authorization = BasicHeader(credential);
                using var r = await _http.SendAsync(request, ct).ConfigureAwait(false);
                return r.IsSuccessStatusCode ? CredentialTestResult.Ok : rejected;
            }

            // Docker Hub needs a scope to validate credentials; any public repository works.
            var scope = normalized == ImageReference.DockerHubRegistry ? "repository:library/hello-world:pull" : challenge.Scope;
            await FetchTokenAsync(challenge with { Scope = scope }, credential, ct).ConfigureAwait(false);
            return CredentialTestResult.Ok;
        }
        catch (RegistryException e)
        {
            return e.Kind == RegistryErrorKind.Unauthorized ? rejected : new CredentialTestResult(false, e.Message);
        }
        catch (HttpRequestException e)
        {
            return new CredentialTestResult(false, $"Could not reach {host}: {e.Message}");
        }
    }

    private static HttpRequestMessage ManifestRequest(HttpMethod method, ImageReference repo, string reference)
    {
        var request = new HttpRequestMessage(method, $"https://{repo.ApiHost}/v2/{repo.Repository}/manifests/{reference}");
        foreach (var mediaType in MediaTypes.AcceptAll)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
        }
        return request;
    }

    /// <summary>Sends a request, answering an auth challenge once (anonymous token or credentials).</summary>
    private async Task<HttpResponseMessage> SendAsync(ImageReference repo, Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        var scopeKey = $"{repo.ApiHost}|repository:{repo.Repository}:pull";

        var request = WithUserAgent(createRequest());
        if (_tokens.TryGetValue(scopeKey, out var token) && token.ExpiresAt > DateTimeOffset.UtcNow)
        {
            request.Headers.Authorization = token.Header;
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new RegistryException(RegistryErrorKind.Network, $"Could not reach {repo.ApiHost}: {e.Message}", inner: e);
        }
        finally
        {
            request.Dispose();
        }

        if (response.StatusCode != HttpStatusCode.Unauthorized) return Track(repo, response);

        var challenge = ParseChallenge(response);
        if (challenge is null) return Track(repo, response);
        response.Dispose();

        var credential = await _credentials.GetAsync(repo.Registry, ct).ConfigureAwait(false);
        AuthenticationHeaderValue header;
        TimeSpan lifetime;

        if (challenge.Scheme == "basic")
        {
            if (credential is null)
            {
                throw new RegistryException(RegistryErrorKind.Unauthorized,
                    $"{repo.Registry} requires a login. Add an account for this registry under Settings › Registry accounts.", HttpStatusCode.Unauthorized);
            }
            header = BasicHeader(credential);
            lifetime = TimeSpan.FromHours(1);
        }
        else
        {
            var scope = challenge.Scope ?? $"repository:{repo.Repository}:pull";
            try
            {
                (header, lifetime) = await FetchTokenAsync(challenge with { Scope = scope }, credential, ct).ConfigureAwait(false);
            }
            catch (RegistryException e) when (e.Kind == RegistryErrorKind.Unauthorized && credential is not null)
            {
                // Saved credentials are wrong or expired: public images still work anonymously.
                (header, lifetime) = await FetchTokenAsync(challenge with { Scope = scope }, null, ct).ConfigureAwait(false);
                credential = null;
            }
        }

        _authenticatedRegistries[repo.Registry] = credential is not null;
        _tokens[scopeKey] = new CachedToken(header, DateTimeOffset.UtcNow + lifetime - TimeSpan.FromSeconds(15));

        var retry = WithUserAgent(createRequest());
        retry.Headers.Authorization = header;
        try
        {
            return Track(repo, await _http.SendAsync(retry, ct).ConfigureAwait(false));
        }
        catch (HttpRequestException e)
        {
            throw new RegistryException(RegistryErrorKind.Network, $"Could not reach {repo.ApiHost}: {e.Message}", inner: e);
        }
        finally
        {
            retry.Dispose();
        }
    }

    private async Task<(AuthenticationHeaderValue Header, TimeSpan Lifetime)> FetchTokenAsync(AuthChallenge challenge, RegistryCredential? credential, CancellationToken ct)
    {
        if (challenge.Realm is null)
        {
            throw new RegistryException(RegistryErrorKind.InvalidResponse, "The registry sent an auth challenge without realm.");
        }

        HttpRequestMessage request;
        if (credential is { IsIdentityToken: true })
        {
            // OAuth2 refresh token flow (e.g. Azure Container Registry identity tokens).
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credential.Secret,
                ["client_id"] = "image-digest-advisor",
            };
            if (challenge.Service is not null) form["service"] = challenge.Service;
            if (challenge.Scope is not null) form["scope"] = challenge.Scope;
            request = new HttpRequestMessage(HttpMethod.Post, challenge.Realm) { Content = new FormUrlEncodedContent(form) };
        }
        else
        {
            var query = new List<string>();
            if (challenge.Service is not null) query.Add("service=" + Uri.EscapeDataString(challenge.Service));
            if (challenge.Scope is not null) query.Add("scope=" + Uri.EscapeDataString(challenge.Scope));
            var separator = challenge.Realm.Contains('?') ? "&" : "?";
            request = new HttpRequestMessage(HttpMethod.Get, challenge.Realm + (query.Count > 0 ? separator + string.Join("&", query) : string.Empty));
            if (credential is not null) request.Headers.Authorization = BasicHeader(credential);
        }

        using (request)
        {
            WithUserAgent(request);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new RegistryException(RegistryErrorKind.Unauthorized, "The registry rejected the credentials.", response.StatusCode);
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new RegistryException(RegistryErrorKind.InvalidResponse, $"Token request failed: {(int)response.StatusCode} {response.ReasonPhrase}", response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var token = (root.TryGetProperty("token", out var t) ? t.GetString() : null)
                        ?? (root.TryGetProperty("access_token", out var at) ? at.GetString() : null)
                        ?? throw new RegistryException(RegistryErrorKind.InvalidResponse, "The token response contained no token.");
            var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var secs) && secs > 0 ? secs : 60;
            return (new AuthenticationHeaderValue("Bearer", token), TimeSpan.FromSeconds(expiresIn));
        }
    }

    private HttpResponseMessage Track(ImageReference repo, HttpResponseMessage response)
    {
        if (repo.IsDockerHub
            && response.Headers.TryGetValues("ratelimit-remaining", out var remainingValues))
        {
            var (remaining, window) = ParseRateLimit(remainingValues.FirstOrDefault());
            int? limit = response.Headers.TryGetValues("ratelimit-limit", out var limitValues) ? ParseRateLimit(limitValues.FirstOrDefault()).Value : null;
            DockerHubRateLimit = new RateLimitInfo(limit, remaining, window, _authenticatedRegistries.TryGetValue(repo.Registry, out var a) && a);
        }
        return response;
    }

    internal static (int? Value, int? Window) ParseRateLimit(string? header)
    {
        // Format: "100;w=21600"
        if (string.IsNullOrWhiteSpace(header)) return (null, null);
        var parts = header.Split(';', StringSplitOptions.TrimEntries);
        int? value = int.TryParse(parts[0], out var v) ? v : null;
        int? window = parts.Skip(1).Select(p => p.StartsWith("w=", StringComparison.Ordinal) && int.TryParse(p[2..], out var w) ? w : (int?)null).FirstOrDefault(w => w is not null);
        return (value, window);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, ImageReference repo, string reference)
    {
        if (response.IsSuccessStatusCode) return;

        var body = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var what = Digests.IsDigest(reference) ? Digests.Short(reference) : reference;

        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new RegistryException(RegistryErrorKind.NotFound,
                $"'{what}' was not found in {repo.FriendlyName}.", response.StatusCode),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new RegistryException(RegistryErrorKind.Unauthorized,
                $"Access to {repo.FriendlyName} was denied. The repository may be private or not exist. Add an account for {repo.Registry} under Settings › Registry accounts.", response.StatusCode),
            HttpStatusCode.TooManyRequests => new RegistryException(RegistryErrorKind.RateLimited,
                repo.IsDockerHub
                    ? "Docker Hub rate limit reached. Add a Docker Hub account (free) under Settings › Registry accounts to raise the limit, or wait a while."
                    : $"{repo.Registry} reports too many requests. Please wait a while.", response.StatusCode),
            _ => new RegistryException(RegistryErrorKind.InvalidResponse,
                $"{repo.Registry} returned {(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body)}", response.StatusCode),
        };
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] + "…" : s;

    internal static AuthChallenge? ParseChallenge(HttpResponseMessage response)
    {
        foreach (var header in response.Headers.WwwAuthenticate)
        {
            var scheme = header.Scheme.ToLowerInvariant();
            if (scheme is not ("bearer" or "basic")) continue;

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in ChallengeParam().Matches(header.Parameter ?? string.Empty))
            {
                parameters[m.Groups["k"].Value] = m.Groups["v"].Value;
            }

            return new AuthChallenge(
                scheme,
                parameters.GetValueOrDefault("realm"),
                parameters.GetValueOrDefault("service"),
                parameters.GetValueOrDefault("scope"));
        }
        return null;
    }

    private static string? NextLink(HttpResponseMessage response, string currentUrl)
    {
        // Link: </v2/foo/tags/list?n=1000&last=bar>; rel="next"
        if (!response.Headers.TryGetValues("Link", out var links)) return null;
        foreach (var link in links)
        {
            var m = LinkNext().Match(link);
            if (!m.Success) continue;
            return new Uri(new Uri(currentUrl), m.Groups["url"].Value).ToString();
        }
        return null;
    }

    private static AuthenticationHeaderValue BasicHeader(RegistryCredential c) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.Username}:{c.Secret}")));

    private static HttpRequestMessage WithUserAgent(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return request;
    }

    internal sealed record AuthChallenge(string Scheme, string? Realm, string? Service, string? Scope);

    private sealed record CachedToken(AuthenticationHeaderValue Header, DateTimeOffset ExpiresAt);

    [GeneratedRegex("(?<k>[A-Za-z]+)=\"(?<v>[^\"]*)\"")]
    private static partial Regex ChallengeParam();

    [GeneratedRegex("<(?<url>[^>]+)>\\s*;\\s*rel=\"?next\"?")]
    private static partial Regex LinkNext();
}
