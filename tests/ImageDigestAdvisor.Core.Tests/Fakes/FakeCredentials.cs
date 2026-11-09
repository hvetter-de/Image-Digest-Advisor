using ImageDigestAdvisor.Core.Credentials;

namespace ImageDigestAdvisor.Core.Tests.Fakes;

internal sealed class InMemoryCredentialStore : IAppCredentialStore
{
    private readonly Dictionary<string, RegistryCredential> _items = [];

    public Task<RegistryCredential?> GetAsync(string registry, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.GetValueOrDefault(RegistryNames.Normalize(registry)));

    public Task<IReadOnlyList<RegistryAccount>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RegistryAccount>>(_items.Values.Select(c => new RegistryAccount(c.Registry, c.Username, c.Source)).ToList());

    public void Save(string registry, string username, string secret) =>
        _items[RegistryNames.Normalize(registry)] = new RegistryCredential(RegistryNames.Normalize(registry), username, secret, CredentialSource.App);

    public void Remove(string registry) => _items.Remove(RegistryNames.Normalize(registry));
}

internal sealed class ThrowingCredentialProvider : ICredentialProvider
{
    public Task<RegistryCredential?> GetAsync(string registry, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("broken provider");

    public Task<IReadOnlyList<RegistryAccount>> ListAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("broken provider");
}
