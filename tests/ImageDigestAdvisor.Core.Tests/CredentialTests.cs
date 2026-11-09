using System.Text;
using ImageDigestAdvisor.Core.Credentials;
using ImageDigestAdvisor.Core.Tests.Fakes;

namespace ImageDigestAdvisor.Core.Tests;

[TestClass]
public sealed class CredentialTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "imagedigestadvisor-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [TestMethod]
    public async Task MissingDockerConfigMeansNoCredentials()
    {
        var provider = new DockerConfigCredentialProvider(Path.Combine(_dir, "does-not-exist.json"));

        Assert.IsNull(await provider.GetAsync("docker.io"));
        Assert.IsEmpty(await provider.ListAsync());
    }

    [TestMethod]
    public async Task InvalidDockerConfigIsIgnored()
    {
        var provider = new DockerConfigCredentialProvider(WriteConfig("{ not json"));
        Assert.IsNull(await provider.GetAsync("docker.io"));
    }

    [TestMethod]
    public async Task ReadsInlineAuthForDockerHubLegacyKey()
    {
        var path = WriteConfig($$"""{ "auths": { "https://index.docker.io/v1/": { "auth": "{{B64("hvetter:secret")}}" } } }""");
        var provider = new DockerConfigCredentialProvider(path);

        var c = await provider.GetAsync("docker.io");

        Assert.IsNotNull(c);
        Assert.AreEqual("hvetter", c.Username);
        Assert.AreEqual("secret", c.Secret);
        Assert.AreEqual(CredentialSource.DockerConfig, c.Source);
    }

    [TestMethod]
    public async Task ReadsIdentityToken()
    {
        var path = WriteConfig("""{ "auths": { "myacr.azurecr.io": { "identitytoken": "refresh" } } }""");
        var c = await new DockerConfigCredentialProvider(path).GetAsync("myacr.azurecr.io");

        Assert.IsNotNull(c);
        Assert.IsTrue(c.IsIdentityToken);
        Assert.AreEqual("refresh", c.Secret);
    }

    [TestMethod]
    public async Task UsesCredsStoreHelperWhenInstalled()
    {
        var path = WriteConfig("""{ "auths": { "https://index.docker.io/v1/": {} }, "credsStore": "desktop" }""");
        string? askedServer = null;
        var provider = new DockerConfigCredentialProvider(
            path,
            helper => helper == "desktop" ? "C:/fake/docker-credential-desktop.exe" : null,
            (_, command, input, _) =>
            {
                if (command == "list") return Task.FromResult<string?>("""{ "https://index.docker.io/v1/": "hvetter", "ghcr.io": "octo" }""");
                askedServer = input;
                return Task.FromResult<string?>("""{ "ServerURL": "https://index.docker.io/v1/", "Username": "hvetter", "Secret": "pat" }""");
            });

        var c = await provider.GetAsync("docker.io");
        var accounts = await provider.ListAsync();

        Assert.AreEqual("https://index.docker.io/v1/", askedServer);
        Assert.AreEqual("pat", c!.Secret);
        CollectionAssert.AreEquivalent(new[] { "docker.io", "ghcr.io" }, accounts.Select(a => a.Registry).ToArray());
        Assert.AreEqual("hvetter", accounts.Single(a => a.Registry == "docker.io").Username);
    }

    [TestMethod]
    public async Task MissingHelperFallsBackToNothing()
    {
        // Docker was uninstalled but config.json still references the helper.
        var path = WriteConfig("""{ "auths": { "https://index.docker.io/v1/": {} }, "credsStore": "desktop" }""");
        var provider = new DockerConfigCredentialProvider(path, _ => null, (_, _, _, _) => throw new AssertFailedException("must not run"));

        Assert.IsNull(await provider.GetAsync("docker.io"));
        Assert.HasCount(1, await provider.ListAsync());
    }

    [TestMethod]
    public async Task ChainPrefersAppCredentialsAndSurvivesBrokenProviders()
    {
        var app = new InMemoryCredentialStore();
        app.Save("https://index.docker.io/v1/", "app-user", "app-secret");
        var docker = new DockerConfigCredentialProvider(
            WriteConfig($$"""{ "auths": { "docker.io": { "auth": "{{B64("docker-user:x")}}" }, "ghcr.io": { "auth": "{{B64("gh:y")}}" } } }"""));
        var chain = new CredentialChain([new ThrowingCredentialProvider(), app, docker]);

        Assert.AreEqual("app-user", (await chain.GetAsync("docker.io"))!.Username);
        Assert.AreEqual("gh", (await chain.GetAsync("ghcr.io"))!.Username);
        Assert.IsNull(await chain.GetAsync("quay.io"));

        var accounts = await chain.ListAsync();
        Assert.AreEqual(CredentialSource.App, accounts.Single(a => a.Registry == "docker.io").Source);
        Assert.AreEqual(CredentialSource.DockerConfig, accounts.Single(a => a.Registry == "ghcr.io").Source);
    }

    [TestMethod]
    [DataRow("https://index.docker.io/v1/", "docker.io")]
    [DataRow("registry-1.docker.io", "docker.io")]
    [DataRow("MyAcr.AzureCR.io", "myacr.azurecr.io")]
    [DataRow("https://ghcr.io", "ghcr.io")]
    public void RegistryNamesAreNormalized(string input, string expected) => Assert.AreEqual(expected, RegistryNames.Normalize(input));

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task WindowsCredentialStoreRoundTrip()
    {
        var store = new WindowsCredentialStore();
        var registry = $"test-{Guid.NewGuid():N}.example.com";
        try
        {
            store.Save(registry, "user", "s3cret-ü");
            var c = await store.GetAsync(registry);
            Assert.AreEqual("user", c!.Username);
            Assert.AreEqual("s3cret-ü", c.Secret);
            Assert.IsTrue((await store.ListAsync()).Any(a => a.Registry == registry));
        }
        finally
        {
            store.Remove(registry);
        }

        Assert.IsNull(await store.GetAsync(registry));
        store.Remove(registry); // removing twice is fine
    }
}
