using System.Text.Json;
using System.Text.Json.Nodes;

namespace ImageDigestAdvisor.Core.Security;

public sealed class VulnerabilityCheckException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An OSV record, reduced to what the app shows.</summary>
public sealed record OsvVulnerability(
    string Id,
    string? Summary,
    string? Details,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<(string Type, string Score)> Severity,
    IReadOnlyList<OsvAffected> Affected);

/// <param name="Ranges">Each range as its events in order, e.g. <c>[("introduced","0"), ("fixed","1.2.3")]</c>.</param>
public sealed record OsvAffected(string Ecosystem, string Name, IReadOnlyList<IReadOnlyList<(string Kind, string Version)>> Ranges, string? Urgency)
{
    public IEnumerable<string> FixedVersions => Ranges.SelectMany(r => r).Where(e => e.Kind == "fixed").Select(e => e.Version);
}

/// <summary>
/// Client for the OSV.dev API (https://google.github.io/osv.dev/api/). Free, no API key.
/// Only package names, versions and ecosystems are sent; never image names or credentials.
/// </summary>
public sealed class OsvClient(HttpClient http, ITimedCache cache)
{
    public const string BaseUrl = "https://api.osv.dev/v1/";
    private const int BatchSize = 1000; // API limit per querybatch request

    /// <summary>How long OSV answers are reused. The database is updated continuously.</summary>
    public static TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Returns the IDs of the vulnerabilities affecting each package, in the same order.</summary>
    public async Task<IReadOnlyList<IReadOnlyList<string>>> QueryAsync(IReadOnlyList<OsvPackage> packages, CancellationToken ct = default)
    {
        var result = new List<IReadOnlyList<string>>(packages.Count);
        foreach (var chunk in packages.Chunk(BatchSize))
        {
            var body = new JsonObject
            {
                ["queries"] = new JsonArray(chunk.Select(p => (JsonNode)new JsonObject
                {
                    ["package"] = new JsonObject { ["name"] = p.Name, ["ecosystem"] = p.Ecosystem },
                    ["version"] = p.Version,
                }).ToArray()),
            }.ToJsonString();

            var response = await PostCachedAsync("querybatch", body, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(response);
            var results = doc.RootElement.GetProperty("results");
            if (results.GetArrayLength() != chunk.Length) throw new VulnerabilityCheckException("osv.dev returned an unexpected number of results.");

            foreach (var r in results.EnumerateArray())
            {
                result.Add(r.TryGetProperty("vulns", out var vulns)
                    ? vulns.EnumerateArray().Select(v => v.GetProperty("id").GetString()!).ToList()
                    : []);
            }
        }
        return result;
    }

    public async Task<OsvVulnerability> GetAsync(string id, CancellationToken ct = default)
    {
        var key = "osv-vuln:" + id;
        var content = await cache.GetAsync(key, CacheDuration, ct).ConfigureAwait(false);
        if (content is null)
        {
            content = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, BaseUrl + "vulns/" + Uri.EscapeDataString(id)), ct).ConfigureAwait(false);
            await cache.SetAsync(key, content, ct).ConfigureAwait(false);
        }
        return Parse(content);
    }

    private async Task<byte[]> PostCachedAsync(string path, string body, CancellationToken ct)
    {
        var key = $"osv-{path}:{body}";
        if (await cache.GetAsync(key, CacheDuration, ct).ConfigureAwait(false) is { } cached) return cached;

        var content = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        }, ct).ConfigureAwait(false);
        await cache.SetAsync(key, content, ct).ConfigureAwait(false);
        return content;
    }

    private async Task<byte[]> SendAsync(Func<HttpRequestMessage> create, CancellationToken ct)
    {
        using var request = create();
        request.Headers.UserAgent.ParseAdd("ImageDigestAdvisor/1.0");
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new VulnerabilityCheckException($"osv.dev returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new VulnerabilityCheckException($"Could not reach osv.dev: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new VulnerabilityCheckException("osv.dev did not answer in time.", e);
        }
    }

    internal static OsvVulnerability Parse(byte[] content)
    {
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        var aliases = new List<string>();
        foreach (var field in new[] { "aliases", "upstream" })
        {
            if (root.TryGetProperty(field, out var a) && a.ValueKind == JsonValueKind.Array)
            {
                aliases.AddRange(a.EnumerateArray().Select(x => x.GetString()).OfType<string>());
            }
        }

        var affected = new List<OsvAffected>();
        if (root.TryGetProperty("affected", out var aff) && aff.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in aff.EnumerateArray())
            {
                if (!a.TryGetProperty("package", out var p)) continue;
                var fixes = new List<IReadOnlyList<(string, string)>>();
                if (a.TryGetProperty("ranges", out var ranges) && ranges.ValueKind == JsonValueKind.Array)
                {
                    foreach (var range in ranges.EnumerateArray())
                    {
                        if (!range.TryGetProperty("events", out var events)) continue;
                        var list = new List<(string, string)>();
                        foreach (var e in events.EnumerateArray())
                        {
                            foreach (var kind in new[] { "introduced", "fixed", "last_affected" })
                            {
                                if (Str(e, kind) is { } version) list.Add((kind, version));
                            }
                        }
                        fixes.Add(list);
                    }
                }
                string? urgency = a.TryGetProperty("ecosystem_specific", out var es) && es.ValueKind == JsonValueKind.Object
                                  && es.TryGetProperty("urgency", out var u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString()
                    : null;
                affected.Add(new OsvAffected(Str(p, "ecosystem") ?? string.Empty, Str(p, "name") ?? string.Empty, fixes, urgency));
            }
        }

        var severity = new List<(string, string)>();
        if (root.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sev.EnumerateArray())
            {
                if (Str(s, "type") is { } type && Str(s, "score") is { } score) severity.Add((type, score));
            }
        }

        return new OsvVulnerability(root.GetProperty("id").GetString()!, Str(root, "summary"), Str(root, "details"), aliases, severity, affected);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
