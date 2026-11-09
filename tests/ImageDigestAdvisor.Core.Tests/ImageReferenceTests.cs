using ImageDigestAdvisor.Core.References;

namespace ImageDigestAdvisor.Core.Tests;

[TestClass]
public sealed class ImageReferenceTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void OfficialImageGetsLibraryPrefixAndDockerHub()
    {
        var r = ImageReference.Parse("nginx:1.27@" + Digest);

        Assert.AreEqual("docker.io", r.Registry);
        Assert.AreEqual("library/nginx", r.Repository);
        Assert.AreEqual("1.27", r.Tag);
        Assert.AreEqual(Digest, r.Digest);
        Assert.AreEqual("registry-1.docker.io", r.ApiHost);
        Assert.AreEqual("nginx:1.27@" + Digest, r.ToString());
    }

    [TestMethod]
    public void BareNameDefaultsToLatest()
    {
        var r = ImageReference.Parse("redis");
        Assert.AreEqual("latest", r.Tag);
        Assert.IsNull(r.Digest);
    }

    [TestMethod]
    public void DigestWithoutTagHasNoTag()
    {
        var r = ImageReference.Parse("nginx@" + Digest);
        Assert.IsNull(r.Tag);
        Assert.AreEqual(Digest, r.Digest);
    }

    [TestMethod]
    [DataRow("mcr.microsoft.com/dotnet/aspnet:8.0", "mcr.microsoft.com", "dotnet/aspnet", "8.0")]
    [DataRow("ghcr.io/owner/app:v1.2.3", "ghcr.io", "owner/app", "v1.2.3")]
    [DataRow("localhost:5000/team/app:dev", "localhost:5000", "team/app", "dev")]
    [DataRow("bitnami/redis:7.2", "docker.io", "bitnami/redis", "7.2")]
    [DataRow("docker.io/library/nginx:1", "docker.io", "library/nginx", "1")]
    [DataRow("index.docker.io/library/nginx:1", "docker.io", "library/nginx", "1")]
    public void ParsesRegistryRepositoryAndTag(string input, string registry, string repository, string tag)
    {
        var r = ImageReference.Parse(input);
        Assert.AreEqual(registry, r.Registry);
        Assert.AreEqual(repository, r.Repository);
        Assert.AreEqual(tag, r.Tag);
    }

    [TestMethod]
    [DataRow("FROM nginx:1.27 AS build")]
    [DataRow("docker pull nginx:1.27")]
    [DataRow("  nginx:1.27  ")]
    public void AcceptsPastedCommands(string input)
    {
        var r = ImageReference.Parse(input);
        Assert.AreEqual("library/nginx", r.Repository);
        Assert.AreEqual("1.27", r.Tag);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("nginx@sha256:123")]
    [DataRow("Nginx:1")]
    [DataRow("nginx:bad tag")]
    public void RejectsInvalidInput(string input)
    {
        Assert.IsFalse(ImageReference.TryParse(input, out _, out var error));
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [TestMethod]
    public void UppercaseDigestIsNormalized()
    {
        var r = ImageReference.Parse("nginx@" + Digest.ToUpperInvariant().Replace("SHA256", "sha256"));
        Assert.AreEqual(Digest, r.Digest);
    }
}
