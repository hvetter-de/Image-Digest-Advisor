namespace ImageDigestAdvisor.Core.Security;

/// <summary>
/// A package URL (purl) as found in SBOMs, e.g.
/// <c>pkg:deb/debian/zlib@1:1.3.1-1?os_distro=trixie&amp;os_name=debian&amp;os_version=13</c>.
/// See https://github.com/package-url/purl-spec.
/// </summary>
public sealed record PackageUrl(string Type, string? Namespace, string Name, string? Version, IReadOnlyDictionary<string, string> Qualifiers)
{
    public string? Qualifier(string key) => Qualifiers.TryGetValue(key, out var v) ? v : null;

    public static bool TryParse(string? text, out PackageUrl purl)
    {
        purl = null!;
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("pkg:", StringComparison.OrdinalIgnoreCase)) return false;

        var rest = text[4..].TrimStart('/');
        var hash = rest.IndexOf('#');
        if (hash >= 0) rest = rest[..hash];

        var qualifiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var question = rest.IndexOf('?');
        if (question >= 0)
        {
            foreach (var pair in rest[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                qualifiers[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            rest = rest[..question];
        }

        // The version follows the last '@' after the last '/' ('@' inside names is percent-encoded).
        string? version = null;
        var lastSlash = rest.LastIndexOf('/');
        var at = rest.LastIndexOf('@');
        if (at > lastSlash)
        {
            version = Uri.UnescapeDataString(rest[(at + 1)..]);
            rest = rest[..at];
        }

        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length < 2) return false;

        purl = new PackageUrl(
            segments[0].ToLowerInvariant(),
            segments.Length > 2 ? string.Join('/', segments[1..^1]) : null,
            segments[^1],
            string.IsNullOrEmpty(version) ? null : version,
            qualifiers);
        return true;
    }

    public override string ToString() =>
        $"pkg:{Type}/{(Namespace is null ? string.Empty : Namespace + "/")}{Name}{(Version is null ? string.Empty : "@" + Version)}";
}
