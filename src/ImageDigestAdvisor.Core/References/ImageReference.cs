using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ImageDigestAdvisor.Core.References;

/// <summary>
/// A parsed container image reference such as <c>nginx:1.27@sha256:...</c> or
/// <c>mcr.microsoft.com/dotnet/aspnet:8.0</c>.
/// </summary>
public sealed partial record ImageReference
{
    public const string DockerHubRegistry = "docker.io";

    /// <summary>Registry host as the user knows it, e.g. <c>docker.io</c>, <c>mcr.microsoft.com</c>, <c>localhost:5000</c>.</summary>
    public required string Registry { get; init; }

    /// <summary>Repository path, e.g. <c>library/nginx</c> or <c>dotnet/aspnet</c>.</summary>
    public required string Repository { get; init; }

    public string? Tag { get; init; }

    /// <summary>Digest in the form <c>sha256:&lt;64 hex&gt;</c>.</summary>
    public string? Digest { get; init; }

    public bool IsDockerHub => Registry == DockerHubRegistry;

    /// <summary>Host used for the OCI distribution API (Docker Hub uses a different host than its name).</summary>
    public string ApiHost => IsDockerHub ? "registry-1.docker.io" : Registry;

    /// <summary>Repository as users usually type it (Docker Hub official images without <c>library/</c>).</summary>
    public string FriendlyName
    {
        get
        {
            if (IsDockerHub)
            {
                return Repository.StartsWith("library/", StringComparison.Ordinal)
                    ? Repository["library/".Length..]
                    : Repository;
            }
            return $"{Registry}/{Repository}";
        }
    }

    public ImageReference WithTag(string? tag) => this with { Tag = tag };

    public ImageReference WithDigest(string? digest) => this with { Digest = digest };

    public override string ToString()
    {
        var s = FriendlyName;
        if (Tag is not null) s += ":" + Tag;
        if (Digest is not null) s += "@" + Digest;
        return s;
    }

    public static ImageReference Parse(string input)
    {
        if (!TryParse(input, out var reference, out var error))
        {
            throw new FormatException(error);
        }
        return reference;
    }

    public static bool TryParse(string? input, [NotNullWhen(true)] out ImageReference? reference, out string? error)
    {
        reference = null;
        error = null;

        var text = input?.Trim() ?? string.Empty;

        // Allow pasting a full "docker pull ..." command or a Dockerfile "FROM ..." line.
        text = StripPrefix(text, "docker pull ");
        text = StripPrefix(text, "FROM ");
        var asIndex = text.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
        if (asIndex > 0) text = text[..asIndex];
        text = text.Trim();

        if (text.Length == 0)
        {
            error = "Please enter an image reference, e.g. nginx:1.27@sha256:…";
            return false;
        }

        if (text.Contains(' '))
        {
            error = "An image reference must not contain spaces.";
            return false;
        }

        string? digest = null;
        var at = text.IndexOf('@');
        if (at >= 0)
        {
            digest = text[(at + 1)..].ToLowerInvariant();
            text = text[..at];
            if (!DigestRegex().IsMatch(digest))
            {
                error = $"'{digest}' is not a valid digest. Expected sha256: followed by 64 hex characters.";
                return false;
            }
        }

        string? tag = null;
        var lastSlash = text.LastIndexOf('/');
        var lastColon = text.LastIndexOf(':');
        if (lastColon > lastSlash)
        {
            tag = text[(lastColon + 1)..];
            text = text[..lastColon];
            if (!TagRegex().IsMatch(tag))
            {
                error = $"'{tag}' is not a valid tag.";
                return false;
            }
        }

        string registry;
        string repository;
        var firstSlash = text.IndexOf('/');
        var firstPart = firstSlash >= 0 ? text[..firstSlash] : string.Empty;
        if (firstSlash >= 0 && (firstPart.Contains('.') || firstPart.Contains(':') || firstPart == "localhost"))
        {
            registry = firstPart.ToLowerInvariant();
            repository = text[(firstSlash + 1)..];
        }
        else
        {
            registry = DockerHubRegistry;
            repository = text;
        }

        if (registry is "index.docker.io" or "registry-1.docker.io" or "registry.hub.docker.com")
        {
            registry = DockerHubRegistry;
        }

        if (registry == DockerHubRegistry && !repository.Contains('/'))
        {
            repository = "library/" + repository;
        }

        if (repository.Length == 0 || !RepositoryRegex().IsMatch(repository))
        {
            error = $"'{repository}' is not a valid repository name (only lowercase letters, digits and separators are allowed).";
            return false;
        }

        if (tag is null && digest is null)
        {
            tag = "latest";
        }

        reference = new ImageReference { Registry = registry, Repository = repository, Tag = tag, Digest = digest };
        return true;
    }

    private static string StripPrefix(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..].TrimStart() : text;

    [GeneratedRegex("^sha256:[a-f0-9]{64}$")]
    private static partial Regex DigestRegex();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex TagRegex();

    [GeneratedRegex("^[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*$")]
    private static partial Regex RepositoryRegex();
}
