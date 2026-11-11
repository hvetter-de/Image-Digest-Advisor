using System.Text.RegularExpressions;

namespace ImageDigestAdvisor.Core.Security;

/// <summary>A package as OSV.dev knows it: ecosystem (e.g. <c>Debian:13</c>), name and version.</summary>
public sealed partial record OsvPackage(string Ecosystem, string Name, string Version)
{
    private static readonly Dictionary<string, string> DebianCodenames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["buster"] = "10",
        ["bullseye"] = "11",
        ["bookworm"] = "12",
        ["trixie"] = "13",
        ["forky"] = "14",
    };

    /// <summary>
    /// Maps a purl from an SBOM to the OSV ecosystem and package name. Returns null for package types
    /// OSV can't check (e.g. the image itself, or rpm, whose distribution naming is not supported yet).
    /// OS packages are mapped to their <b>source</b> package, because Debian, Ubuntu and Alpine publish
    /// their security data per source package (<c>libc6</c> → <c>glibc</c>).
    /// </summary>
    public static OsvPackage? FromPurl(PackageUrl purl)
    {
        if (purl.Version is null) return null;

        switch (purl.Type)
        {
            case "deb":
            {
                var (name, version) = SourcePackage(purl, "upstream");
                var distro = purl.Namespace?.ToLowerInvariant();
                if (distro == "debian" && DebianRelease(purl) is { } debian) return new OsvPackage($"Debian:{debian}", name, version);
                if (distro == "ubuntu" && OsVersion(purl, "ubuntu") is { } ubuntu) return new OsvPackage(UbuntuEcosystem(ubuntu), name, version);
                return null;
            }
            case "apk":
            {
                var (name, version) = SourcePackage(purl, "upstream");
                if (purl.Namespace?.Equals("alpine", StringComparison.OrdinalIgnoreCase) == true
                    && OsVersion(purl, "alpine") is { } alpine
                    && MajorMinor().Match(alpine) is { Success: true } m)
                {
                    return new OsvPackage($"Alpine:v{m.Value}", name, version);
                }
                return null;
            }
            case "npm": return new OsvPackage("npm", Join(purl, "/"), purl.Version);
            case "pypi": return new OsvPackage("PyPI", purl.Name, purl.Version);
            case "golang": return new OsvPackage("Go", Join(purl, "/"), purl.Version);
            case "maven": return purl.Namespace is null ? null : new OsvPackage("Maven", $"{purl.Namespace}:{purl.Name}", purl.Version);
            case "nuget": return new OsvPackage("NuGet", purl.Name, purl.Version);
            case "gem": return new OsvPackage("RubyGems", purl.Name, purl.Version);
            case "cargo": return new OsvPackage("crates.io", purl.Name, purl.Version);
            case "composer": return new OsvPackage("Packagist", Join(purl, "/"), purl.Version);
            default: return null;
        }
    }

    private static string Join(PackageUrl purl, string separator) =>
        purl.Namespace is null ? purl.Name : purl.Namespace + separator + purl.Name;

    /// <summary>Syft-style SBOMs name the binary package and give the source as <c>upstream=glibc@2.36-9</c>.</summary>
    private static (string Name, string Version) SourcePackage(PackageUrl purl, string qualifier)
    {
        if (purl.Qualifier(qualifier) is not { Length: > 0 } upstream) return (purl.Name, purl.Version!);

        var at = upstream.IndexOf('@');
        if (at > 0) return (upstream[..at], upstream[(at + 1)..]);

        // "glibc (2.36-9)" is used by some generators.
        var paren = upstream.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && upstream.EndsWith(')')) return (upstream[..paren], upstream[(paren + 2)..^1]);
        return (upstream, purl.Version!);
    }

    private static string? DebianRelease(PackageUrl purl)
    {
        if (OsVersion(purl, "debian") is { } v && Major().Match(v) is { Success: true } m) return m.Value;
        foreach (var key in new[] { "os_distro", "distro" })
        {
            if (purl.Qualifier(key) is { } codename && DebianCodenames.TryGetValue(codename, out var release)) return release;
        }
        return null;
    }

    /// <summary>Docker's SBOMs use <c>os_version=13</c>, Syft uses <c>distro=debian-12</c>.</summary>
    private static string? OsVersion(PackageUrl purl, string distro)
    {
        if (purl.Qualifier("os_version") is { Length: > 0 } v) return v;
        if (purl.Qualifier("distro") is { } d && d.StartsWith(distro + "-", StringComparison.OrdinalIgnoreCase)) return d[(distro.Length + 1)..];
        return null;
    }

    /// <summary>OSV names Ubuntu LTS releases <c>Ubuntu:22.04:LTS</c> and others <c>Ubuntu:24.10</c>.</summary>
    private static string UbuntuEcosystem(string version)
    {
        var m = MajorMinor().Match(version);
        if (!m.Success) return $"Ubuntu:{version}";
        var parts = m.Value.Split('.');
        var lts = parts[1] == "04" && int.TryParse(parts[0], out var year) && year % 2 == 0;
        return lts ? $"Ubuntu:{m.Value}:LTS" : $"Ubuntu:{m.Value}";
    }

    [GeneratedRegex(@"^\d+")]
    private static partial Regex Major();

    [GeneratedRegex(@"^\d+\.\d+")]
    private static partial Regex MajorMinor();
}
