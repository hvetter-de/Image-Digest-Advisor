using System.Text.Json;
using ImageDigestAdvisor.Core.Analysis;
using ImageDigestAdvisor.Core.Credentials;
using ImageDigestAdvisor.Core.References;
using ImageDigestAdvisor.Core.Registry;
using ImageDigestAdvisor.Core.Security;
using ImageDigestAdvisor.Core.Tests.Fakes;

namespace ImageDigestAdvisor.Core.Tests;

[TestClass]
public sealed class SecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    // ---- purl → OSV package ----

    [TestMethod]
    public void ParsesDockerSbomDebianPurl()
    {
        Assert.IsTrue(PackageUrl.TryParse("pkg:deb/debian/attr@1%3A2.5.2-3?os_distro=trixie&os_name=debian&os_version=13", out var purl));
        Assert.AreEqual("deb", purl.Type);
        Assert.AreEqual("debian", purl.Namespace);
        Assert.AreEqual("attr", purl.Name);
        Assert.AreEqual("1:2.5.2-3", purl.Version);
        Assert.AreEqual(new OsvPackage("Debian:13", "attr", "1:2.5.2-3"), OsvPackage.FromPurl(purl));
    }

    [TestMethod]
    public void SyftStyleDebianPurlUsesSourcePackage()
    {
        Assert.IsTrue(PackageUrl.TryParse("pkg:deb/debian/libc6@2.36-9+deb12u4?arch=amd64&upstream=glibc&distro=debian-12", out var purl));
        Assert.AreEqual(new OsvPackage("Debian:12", "glibc", "2.36-9+deb12u4"), OsvPackage.FromPurl(purl));

        Assert.IsTrue(PackageUrl.TryParse("pkg:deb/debian/libpcre3@2%3A8.39-15?upstream=pcre3%402%3A8.39-15&distro=debian-12", out var withVersion));
        Assert.AreEqual(new OsvPackage("Debian:12", "pcre3", "2:8.39-15"), OsvPackage.FromPurl(withVersion));
    }

    [TestMethod]
    [DataRow("pkg:apk/alpine/busybox@1.36.1-r29?arch=x86_64&distro=alpine-3.20.3", "Alpine:v3.20", "busybox", "1.36.1-r29")]
    [DataRow("pkg:deb/ubuntu/openssl@3.0.2-0ubuntu1.18?distro=ubuntu-22.04", "Ubuntu:22.04:LTS", "openssl", "3.0.2-0ubuntu1.18")]
    [DataRow("pkg:deb/ubuntu/openssl@3.3.1-2ubuntu2?os_version=24.10", "Ubuntu:24.10", "openssl", "3.3.1-2ubuntu2")]
    [DataRow("pkg:npm/%40babel/core@7.24.0", "npm", "@babel/core", "7.24.0")]
    [DataRow("pkg:npm/brace-expansion@2.0.2", "npm", "brace-expansion", "2.0.2")]
    [DataRow("pkg:pypi/pip@25.0.1", "PyPI", "pip", "25.0.1")]
    [DataRow("pkg:golang/golang.org/x/net@v0.23.0", "Go", "golang.org/x/net", "v0.23.0")]
    [DataRow("pkg:maven/org.apache.logging.log4j/log4j-core@2.14.1", "Maven", "org.apache.logging.log4j:log4j-core", "2.14.1")]
    [DataRow("pkg:nuget/Newtonsoft.Json@13.0.1", "NuGet", "Newtonsoft.Json", "13.0.1")]
    public void MapsPurlsToOsvEcosystems(string text, string ecosystem, string name, string version)
    {
        Assert.IsTrue(PackageUrl.TryParse(text, out var purl));
        Assert.AreEqual(new OsvPackage(ecosystem, name, version), OsvPackage.FromPurl(purl));
    }

    [TestMethod]
    [DataRow("pkg:rpm/redhat/openssl@3.0.7-27.el9?distro=rhel-9.4")]
    [DataRow("pkg:docker/library/nginx@sha256%3Aabc")]
    [DataRow("pkg:deb/debian/zlib")] // no version
    public void UnsupportedPurlsAreSkipped(string text)
    {
        Assert.IsTrue(PackageUrl.TryParse(text, out var purl));
        Assert.IsNull(OsvPackage.FromPurl(purl));
    }

    // ---- CVSS and versions ----

    [TestMethod]
    [DataRow("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H", 9.8, "Critical")]
    [DataRow("CVSS:3.1/AV:N/AC:H/PR:N/UI:N/S:U/C:N/I:H/A:H", 7.4, "High")]
    [DataRow("CVSS:3.1/AV:L/AC:L/PR:L/UI:N/S:U/C:N/I:N/A:H", 5.5, "Medium")]
    [DataRow("CVSS:3.1/AV:N/AC:L/PR:N/UI:R/S:C/C:L/I:L/A:N", 6.1, "Medium")]
    [DataRow("CVSS:3.1/AV:N/AC:H/PR:N/UI:N/S:U/C:N/I:L/A:N", 3.7, "Low")]
    [DataRow("CVSS:3.0/AV:N/AC:L/PR:N/UI:N/S:C/C:H/I:H/A:H", 10.0, "Critical")]
    public void ComputesCvss3BaseScore(string vector, double score, string rating)
    {
        var cvss = CvssScore.FromOsv("CVSS_V3", vector)!;
        Assert.AreEqual(score, cvss.Score);
        Assert.AreEqual(rating, cvss.Rating);
    }

    [TestMethod]
    public void Cvss4IsKeptButNotScored()
    {
        var cvss = CvssScore.FromOsv("CVSS_V4", "CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:H/SC:N/SI:N/SA:N")!;
        Assert.AreEqual("4.0", cvss.Version);
        Assert.IsNull(cvss.Score);
        Assert.IsNull(cvss.Rating);
    }

    [TestMethod]
    [DataRow("1.3.1-1", "1.3.1-1+deb13u1", -1)]
    [DataRow("2.9.14+dfsg-1.3~deb12u1", "2.9.14+dfsg-1.3~deb12u2", -1)]
    [DataRow("2.9.14+dfsg-1.3~deb12u2", "2.9.14+dfsg-1.3", -1)] // '~' sorts before the release
    [DataRow("1:1.0", "2.0", 1)] // epoch wins
    [DataRow("2.0.10", "2.0.9", 1)]
    [DataRow("v1.2.3", "1.2.3", 0)]
    [DataRow("3.0.16-1~deb12u1", "3.0.17-1~deb12u3", -1)]
    public void ComparesPackageVersions(string a, string b, int expected) =>
        Assert.AreEqual(expected, Math.Sign(PackageVersion.Compare(a, b)));

    [TestMethod]
    public void PicksFixFromTheReleaseLineOfTheInstalledVersion()
    {
        var affected = new List<OsvAffected>
        {
            new("npm", "brace-expansion",
            [
                [("introduced", "0"), ("fixed", "1.1.19")],
                [("introduced", "2.0.0"), ("fixed", "2.1.5")],
                [("introduced", "3.0.0"), ("fixed", "5.0.9")],
            ], null),
        };
        Assert.AreEqual("2.1.5", VulnerabilityScanner.FixedVersionFor("2.0.2", affected));
        Assert.AreEqual("1.1.19", VulnerabilityScanner.FixedVersionFor("1.1.11", affected));
    }

    [TestMethod]
    public void ClassifiesDebianRecords()
    {
        var pkg = new OsvPackage("Debian:13", "apt", "3.0.4");
        var unimportant = OsvClient.Parse(System.Text.Encoding.UTF8.GetBytes(DebianRecord("DEBIAN-CVE-2011-3374", "apt", fixedIn: null, urgency: "unimportant", "CVSS:3.1/AV:N/AC:H/PR:N/UI:N/S:U/C:N/I:L/A:N")));
        var finding = VulnerabilityScanner.Classify(pkg, unimportant);
        Assert.AreEqual(VulnerabilityCategory.Minor, finding.Category);
        Assert.AreEqual("CVE-2011-3374", finding.Id);
        Assert.AreEqual("Low", finding.Severity!.Rating);

        var noFix = OsvClient.Parse(System.Text.Encoding.UTF8.GetBytes(DebianRecord("DEBIAN-CVE-2026-1", "apt", fixedIn: null, urgency: "not yet assigned", null)));
        Assert.AreEqual(VulnerabilityCategory.NoFix, VulnerabilityScanner.Classify(pkg, noFix).Category);

        var fixable = OsvClient.Parse(System.Text.Encoding.UTF8.GetBytes(DebianRecord("DEBIAN-CVE-2026-2", "apt", fixedIn: "3.0.5", urgency: "not yet assigned", null)));
        var f = VulnerabilityScanner.Classify(pkg, fixable);
        Assert.AreEqual(VulnerabilityCategory.Fixable, f.Category);
        Assert.AreEqual("3.0.5", f.FixedVersion);
    }

    // ---- SBOM from the registry + OSV, end to end ----

    [TestMethod]
    public async Task ScansImageWithSbomAttestation()
    {
        var (registry, scanner, client) = Setup();
        var img = registry.AddImage(Now.AddDays(-30), sbomPurls:
        [
            "pkg:deb/debian/zlib@1.3.1-1?os_distro=trixie&os_name=debian&os_version=13",
            "pkg:deb/debian/apt@3.0.4?os_distro=trixie&os_name=debian&os_version=13",
            "pkg:docker/team/web@1.0", // the image itself, not checkable
        ]);
        registry.Tag("team/web", "1.0", img.IndexDigest);
        AddOsv(registry, "Debian:13|zlib|1.3.1-1", DebianRecord("DEBIAN-CVE-2026-76844", "zlib", "1.3.1-1+deb13u1", "not yet assigned", "CVSS:3.1/AV:N/AC:H/PR:N/UI:N/S:U/C:N/I:H/A:H"));
        AddOsv(registry, "Debian:13|apt|3.0.4", DebianRecord("DEBIAN-CVE-2011-3374", "apt", null, "unimportant", null));

        var details = await Inspect(client, "1.0");
        var report = await scanner.ScanAsync(details);

        Assert.AreEqual(VulnerabilityScanStatus.Completed, report.Status, report.Message);
        Assert.AreEqual(3, report.PackageCount);
        Assert.AreEqual(2, report.CheckedPackageCount);
        Assert.AreEqual(1, report.FixableCount);
        Assert.AreEqual(1, report.MinorCount);
        Assert.AreEqual("CVE-2026-76844", report.Findings[0].Id);
        Assert.AreEqual(7.4, report.Findings[0].Severity!.Score);

        // Second scan is served from the cache: no further OSV requests.
        var before = registry.OsvRequests;
        await scanner.ScanAsync(details);
        Assert.AreEqual(before, registry.OsvRequests);
    }

    [TestMethod]
    public async Task ComparesReportsOfTwoImages()
    {
        var (registry, scanner, client) = Setup();
        var old = registry.AddImage(Now.AddDays(-60), salt: "old", sbomPurls: ["pkg:deb/debian/zlib@1.3.1-1?os_version=13"]);
        var updated = registry.AddImage(Now.AddDays(-1), salt: "new", sbomPurls: ["pkg:deb/debian/zlib@1.3.1-1+deb13u1?os_version=13"]);
        registry.Tag("team/web", "old", old.IndexDigest);
        registry.Tag("team/web", "new", updated.IndexDigest);
        AddOsv(registry, "Debian:13|zlib|1.3.1-1", DebianRecord("DEBIAN-CVE-2026-76844", "zlib", "1.3.1-1+deb13u1", null, null));

        var oldReport = await scanner.ScanAsync(await Inspect(client, "old"));
        var newReport = await scanner.ScanAsync(await Inspect(client, "new"));

        Assert.AreEqual(1, oldReport.FixableCount);
        Assert.AreEqual(0, newReport.Findings.Count);
        Assert.AreEqual("CVE-2026-76844", newReport.FixedComparedTo(oldReport).Single().Id);
        Assert.AreEqual(0, newReport.NewComparedTo(oldReport).Count);
        Assert.AreEqual(0, newReport.StillOpenComparedTo(oldReport).Count);
    }

    [TestMethod]
    public async Task ListsFixableVulnerabilitiesThatAreStillOpen()
    {
        var (registry, scanner, client) = Setup();
        // zlib gets fixed in the update, curl doesn't; apt's issue has no fix and isn't counted as "still open".
        var old = registry.AddImage(Now.AddDays(-60), salt: "old", sbomPurls:
        [
            "pkg:deb/debian/zlib@1.3.1-1?os_version=13",
            "pkg:deb/debian/curl@8.14.1-2?os_version=13",
            "pkg:deb/debian/apt@3.0.4?os_version=13",
        ]);
        var updated = registry.AddImage(Now.AddDays(-1), salt: "new", sbomPurls:
        [
            "pkg:deb/debian/zlib@1.3.1-1+deb13u1?os_version=13",
            "pkg:deb/debian/curl@8.14.1-2?os_version=13",
            "pkg:deb/debian/apt@3.0.4?os_version=13",
        ]);
        registry.Tag("team/web", "old", old.IndexDigest);
        registry.Tag("team/web", "new", updated.IndexDigest);
        AddOsv(registry, "Debian:13|zlib|1.3.1-1", DebianRecord("DEBIAN-CVE-2026-76844", "zlib", "1.3.1-1+deb13u1", null, null));
        AddOsv(registry, "Debian:13|curl|8.14.1-2", DebianRecord("DEBIAN-CVE-2026-50000", "curl", "8.14.1-2+deb13u1", null, null));
        AddOsv(registry, "Debian:13|apt|3.0.4", DebianRecord("DEBIAN-CVE-2011-3374", "apt", null, "unimportant", null));

        var oldReport = await scanner.ScanAsync(await Inspect(client, "old"));
        var newReport = await scanner.ScanAsync(await Inspect(client, "new"));

        Assert.AreEqual(2, oldReport.FixableCount);
        Assert.AreEqual("CVE-2026-76844", newReport.FixedComparedTo(oldReport).Single().Id);
        var stillOpen = newReport.StillOpenComparedTo(oldReport).Single();
        Assert.AreEqual("CVE-2026-50000", stillOpen.Id);
        Assert.AreEqual("8.14.1-2+deb13u1", stillOpen.FixedVersion);
    }

    [TestMethod]
    public async Task ImageWithoutSbomReportsNoSbom()
    {
        var (registry, scanner, client) = Setup();
        var img = registry.AddImage(Now.AddDays(-5));
        registry.Tag("team/web", "1.0", img.IndexDigest);

        var report = await scanner.ScanAsync(await Inspect(client, "1.0"));

        Assert.AreEqual(VulnerabilityScanStatus.NoSbom, report.Status);
        Assert.AreEqual(0, registry.OsvRequests);
    }

    [TestMethod]
    public async Task OsvErrorBecomesFailedReport()
    {
        var (registry, scanner, client) = Setup();
        var img = registry.AddImage(Now.AddDays(-5), sbomPurls: ["pkg:deb/debian/zlib@1.3.1-1?os_version=13"]);
        registry.Tag("team/web", "1.0", img.IndexDigest);
        registry.OsvAffects["Debian:13|zlib|1.3.1-1"] = ["MISSING-1"]; // record lookup returns 404

        var report = await scanner.ScanAsync(await Inspect(client, "1.0"));

        Assert.AreEqual(VulnerabilityScanStatus.Failed, report.Status);
        StringAssert.Contains(report.Message, "osv.dev");
    }

    private static (FakeRegistry Registry, VulnerabilityScanner Scanner, RegistryClient Client) Setup()
    {
        var registry = new FakeRegistry();
        var http = new HttpClient(registry);
        var client = new RegistryClient(http, new CredentialChain([new InMemoryCredentialStore()]), new MemoryContentCache());
        var scanner = new VulnerabilityScanner(new SbomReader(client), new OsvClient(http, new MemoryTimedCache()));
        return (registry, scanner, client);
    }

    private static Task<ImageDetails> Inspect(RegistryClient client, string tag) =>
        new ImageInspector(client).InspectAsync(ImageReference.Parse("registry.example.com/team/web"), tag, Platform.LinuxAmd64);

    private static void AddOsv(FakeRegistry registry, string packageKey, string record)
    {
        var id = JsonDocument.Parse(record).RootElement.GetProperty("id").GetString()!;
        registry.OsvRecords[id] = record;
        if (!registry.OsvAffects.TryGetValue(packageKey, out var list)) registry.OsvAffects[packageKey] = list = [];
        list.Add(id);
    }

    private static string DebianRecord(string id, string package, string? fixedIn, string? urgency, string? cvss3) =>
        JsonSerializer.Serialize(new
        {
            id,
            details = "Something bad. More details follow.",
            upstream = new[] { id.Replace("DEBIAN-", string.Empty, StringComparison.Ordinal) },
            severity = cvss3 is null ? Array.Empty<object>() : [new { type = "CVSS_V3", score = cvss3 }],
            affected = new[]
            {
                new
                {
                    package = new { name = package, ecosystem = "Debian:13" },
                    ranges = new[]
                    {
                        new
                        {
                            type = "ECOSYSTEM",
                            events = fixedIn is null
                                ? new object[] { new { introduced = "0" } }
                                : [new { introduced = "0" }, new { @fixed = fixedIn }],
                        },
                    },
                    ecosystem_specific = new { urgency },
                },
            },
        });
}
