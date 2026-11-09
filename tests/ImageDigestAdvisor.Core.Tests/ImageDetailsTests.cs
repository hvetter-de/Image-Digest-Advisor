using System.Text;
using ImageDigestAdvisor.Core.Analysis;
using ImageDigestAdvisor.Core.References;
using ImageDigestAdvisor.Core.Registry;

namespace ImageDigestAdvisor.Core.Tests;

[TestClass]
public sealed class ImageDetailsTests
{
    private const string Config = """
        {
          "created": "2026-11-05T22:59:00Z",
          "os": "linux",
          "architecture": "amd64",
          "config": {
            "User": "app",
            "WorkingDir": "/app",
            "Entrypoint": ["dotnet", "App.dll"],
            "Cmd": null,
            "ExposedPorts": { "8080/tcp": {} },
            "Env": [
              "PATH=/usr/local/sbin:/usr/bin",
              "APP_UID=1654",
              "DOTNET_VERSION=10.0.14",
              "ASPNET_VERSION=10.0.14",
              "DOTNET_SDK_VERSION=10.0.403",
              "DOTNET_GENERATE_ASPNET_CERTIFICATE_VERSION=false",
              "CUSTOM_VERSION=v2.1"
            ],
            "Labels": { "org.opencontainers.image.version": "24.04" }
          },
          "history": [
            { "created": "2026-10-01T10:00:00Z", "created_by": "/bin/sh -c #(nop) ADD file:abc in / " },
            { "created": "2026-11-05T22:58:00Z", "created_by": "ENV DOTNET_VERSION=10.0.14", "empty_layer": true }
          ]
        }
        """;

    [TestMethod]
    public void ParsesRuntimeConfigAndHistory()
    {
        var config = ManifestParser.ParseConfig(Encoding.UTF8.GetBytes(Config));

        Assert.AreEqual("app", config.User);
        Assert.AreEqual("/app", config.WorkingDir);
        CollectionAssert.AreEqual(new[] { "dotnet", "App.dll" }, config.Entrypoint.ToArray());
        Assert.IsEmpty(config.Cmd);
        CollectionAssert.AreEqual(new[] { "8080/tcp" }, config.ExposedPorts.ToArray());
        Assert.HasCount(2, config.History);
        Assert.IsTrue(config.History[1].EmptyLayer);
        Assert.AreEqual("ENV DOTNET_VERSION=10.0.14", config.History[1].CreatedBy);
    }

    [TestMethod]
    public void SoftwareVersionsComeFromVersionEnvironmentVariables()
    {
        var details = Details(Config);

        CollectionAssert.AreEqual(
            new[] { "DOTNET_VERSION=10.0.14", "ASPNET_VERSION=10.0.14", "DOTNET_SDK_VERSION=10.0.403", "CUSTOM_VERSION=v2.1" },
            details.SoftwareVersions.Select(v => $"{v.Name}={v.Value}").ToArray(),
            "Non-version values like 'false' and unrelated variables are skipped.");
        Assert.AreEqual(".NET SDK", details.SoftwareVersions[2].DisplayName);
        Assert.AreEqual("CUSTOM_VERSION", details.SoftwareVersions[3].DisplayName);
    }

    [TestMethod]
    public void SoftwareChangesListChangedAddedAndRemovedVersions()
    {
        var older = Details(Config.Replace("10.0.403", "10.0.402").Replace("\"CUSTOM_VERSION=v2.1\"", "\"OLD_VERSION=1.0\""));
        var newer = Details(Config);

        var changes = newer.SoftwareChangesSince(older).Select(c => c.ToString()).ToArray();

        CollectionAssert.AreEqual(new[] { ".NET SDK 10.0.402 → 10.0.403", "CUSTOM_VERSION v2.1 (new)", "OLD_VERSION 1.0 (removed)" }, changes);
    }

    [TestMethod]
    public void SoftwareChangesMessageExplainsRebuildWithoutVersionChanges()
    {
        var message = DigestAnalyzer.SoftwareChangesMessage(Details(Config), Details(Config));

        StringAssert.Contains(message, "unchanged");
        StringAssert.Contains(message, ".NET SDK 10.0.403");
    }

    [TestMethod]
    [DataRow("11.0.0-rc.3.26522.148", true)]
    [DataRow("9.0.0-preview.7.24405.7", true)]
    [DataRow("10.0.14", false)]
    [DataRow("1.27.2-1~bookworm", false)]
    public void DetectsPrereleaseVersions(string value, bool expected) =>
        Assert.AreEqual(expected, new SoftwareVersion("DOTNET_VERSION", value).IsPrerelease);

    [TestMethod]
    public void SoftwareChangesMessageIsNullWithoutVersionInformation()
    {
        var bare = Details("""{ "os": "linux", "architecture": "amd64", "config": { "Env": ["PATH=/bin"] } }""");
        Assert.IsNull(DigestAnalyzer.SoftwareChangesMessage(bare, bare));
    }

    private static ImageDetails Details(string configJson)
    {
        var config = ManifestParser.ParseConfig(Encoding.UTF8.GetBytes(configJson));
        return new ImageDetails
        {
            Reference = ImageReference.Parse("mcr.microsoft.com/dotnet/sdk:10.0"),
            ManifestDigest = "sha256:" + new string('a', 64),
            Platform = Platform.LinuxAmd64,
            Config = config,
            Labels = config.Labels,
        };
    }
}
