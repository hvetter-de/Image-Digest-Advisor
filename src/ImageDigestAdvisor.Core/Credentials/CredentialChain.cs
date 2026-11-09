namespace ImageDigestAdvisor.Core.Credentials;

/// <summary>
/// Tries providers in order (app accounts first, then Docker configuration). When none has
/// credentials the registry is accessed anonymously.
/// </summary>
public sealed class CredentialChain(IEnumerable<ICredentialProvider> providers) : ICredentialProvider
{
    private readonly IReadOnlyList<ICredentialProvider> _providers = providers.ToList();

    public async Task<RegistryCredential?> GetAsync(string registry, CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            try
            {
                var credential = await provider.GetAsync(registry, cancellationToken).ConfigureAwait(false);
                if (credential is not null) return credential;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A broken provider (e.g. missing credential helper) must never block anonymous access.
            }
        }
        return null;
    }

    public async Task<IReadOnlyList<RegistryAccount>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<RegistryAccount>();
        foreach (var provider in _providers)
        {
            try
            {
                foreach (var account in await provider.ListAsync(cancellationToken).ConfigureAwait(false))
                {
                    // First provider wins, so an app account hides a Docker login for the same registry.
                    if (!result.Any(a => a.Registry == account.Registry)) result.Add(account);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
        return result;
    }
}
