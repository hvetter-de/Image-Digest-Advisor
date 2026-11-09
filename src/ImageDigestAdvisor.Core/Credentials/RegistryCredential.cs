namespace ImageDigestAdvisor.Core.Credentials;

public enum CredentialSource
{
    /// <summary>Saved in this app (Windows Credential Manager).</summary>
    App,

    /// <summary>Found in the user's Docker configuration (<c>docker login</c>).</summary>
    DockerConfig,
}

public sealed record RegistryCredential(string Registry, string Username, string Secret, CredentialSource Source)
{
    /// <summary>
    /// Docker stores OAuth refresh tokens (e.g. for Azure Container Registry) with the username <c>&lt;token&gt;</c>.
    /// </summary>
    public bool IsIdentityToken => Username == "<token>";

    public override string ToString() => $"{Username}@{Registry} ({Source})";
}

/// <summary>An account entry shown in the UI. Never contains the secret.</summary>
public sealed record RegistryAccount(string Registry, string Username, CredentialSource Source);

public interface ICredentialProvider
{
    /// <summary>Returns credentials for a registry or null if none are known.</summary>
    Task<RegistryCredential?> GetAsync(string registry, CancellationToken cancellationToken = default);

    /// <summary>Lists accounts (without secrets) this provider knows about.</summary>
    Task<IReadOnlyList<RegistryAccount>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>Credentials stored by the app itself.</summary>
public interface IAppCredentialStore : ICredentialProvider
{
    void Save(string registry, string username, string secret);

    void Remove(string registry);
}

/// <summary>Normalizes registry names so <c>https://index.docker.io/v1/</c>, <c>docker.io</c> etc. match.</summary>
public static class RegistryNames
{
    public static string Normalize(string registry)
    {
        var r = registry.Trim().ToLowerInvariant();
        if (r.StartsWith("https://", StringComparison.Ordinal)) r = r["https://".Length..];
        if (r.StartsWith("http://", StringComparison.Ordinal)) r = r["http://".Length..];
        var slash = r.IndexOf('/');
        if (slash >= 0) r = r[..slash];
        return r switch
        {
            "index.docker.io" or "registry-1.docker.io" or "registry.hub.docker.com" or "hub.docker.com" => "docker.io",
            _ => r,
        };
    }
}
