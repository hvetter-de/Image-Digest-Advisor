using System.Text.Json;
using ImageDigestAdvisor.Core.Analysis;
using ImageDigestAdvisor.Core.Credentials;
using ImageDigestAdvisor.Core.History;
using ImageDigestAdvisor.Core.References;
using ImageDigestAdvisor.Core.Registry;
using ImageDigestAdvisor.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace ImageDigestAdvisor.Core.Tests;

[TestClass]
public sealed class HistoryTests
{
    private const string Repo = "registry.example.com/cosmosdb/emulator";
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private FakeRegistry _registry = null!;
    private FakeTimeProvider _time = null!;
    private DigestHistoryStore _store = null!;
    private TagHistoryService _history = null!;
    private DigestAnalyzer _analyzer = null!;

    [TestInitialize]
    public void Init()
    {
        _registry = new FakeRegistry();
        _time = new FakeTimeProvider(Now);
        var http = new HttpClient(_registry);
        var client = new RegistryClient(http, new CredentialChain([new InMemoryCredentialStore()]), new MemoryContentCache());
        var catalog = new TagCatalog(http, client);
        TagCatalog.CacheDuration = TimeSpan.Zero;
        var inspector = new ImageInspector(client);
        _store = new DigestHistoryStore(null, _time);
        _history = new TagHistoryService(client, catalog, inspector, _store);
        _analyzer = new DigestAnalyzer(client, catalog, inspector, _time);
    }

    // ---- Tag families ----

    private static readonly string[] CosmosTags =
    [
        "2.14.24", "2.14.24-7610c980", "2.14.26", "latest", "mongodb", "stable",
        "vnext-EN20250414", "vnext-EN20250414-2", "vnext-EN20250414-amd64", "vnext-EN20250414-arm64",
        "vnext-EN20261019", "vnext-EN20261123", "vnext-EN20261123-amd64",
        "vnext-latest", "vnext-latest-amd64", "vnext-preview", "vnext-preview-testcontainers",
    ];

    [TestMethod]
    public void ChannelTagFindsDatedReleaseTags()
    {
        var match = TagFamily.Find("vnext-latest", CosmosTags);

        CollectionAssert.AreEqual(
            new[] { "vnext-EN20261123", "vnext-EN20261019", "vnext-EN20250414-2", "vnext-EN20250414" },
            match.Tags.ToArray(),
            "Platform copies (-amd64) and other channels (preview) are not releases.");
        Assert.AreEqual("dated tags like vnext-EN20261123", match.Description);
    }

    [TestMethod]
    public void LatestFindsPlainVersionTags()
    {
        var match = TagFamily.Find("latest", CosmosTags);

        CollectionAssert.AreEqual(new[] { "2.14.26", "2.14.24" }, match.Tags.ToArray());
        Assert.AreEqual("release tags like 2.14.26", match.Description);
    }

    [TestMethod]
    public void VersionTagFindsMoreSpecificVersionsOfTheSameVariant()
    {
        string[] tags = ["1.27.9", "1.27.10", "1.27.10-alpine", "1.28.0", "1.27", "1.2.7", "v1.27.11"];

        CollectionAssert.AreEqual(new[] { "1.27.10", "1.27.9" }, TagFamily.Find("1.27", tags).Tags.ToArray());
        CollectionAssert.AreEqual(new[] { "1.27.10-alpine" }, TagFamily.Find("1.27-alpine", tags).Tags.ToArray());
        Assert.AreEqual("version tags 1.27.x", TagFamily.Find("1.27", tags).Description);
    }

    [TestMethod]
    public void FullySpecificTagHasNoReleaseTags()
    {
        var match = TagFamily.Find("1.27.10", ["1.27.9", "1.27.10", "1.27"]);

        Assert.IsEmpty(match.Tags);
        Assert.IsNull(match.Description);
    }

    // ---- Local record ----

    [TestMethod]
    public void StoreKeepsOneEntryPerPeriodAndPersists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.json");
        try
        {
            var repo = ImageReference.Parse("nginx");
            var store = new DigestHistoryStore(path, _time);
            store.Record(repo, "latest", "sha256:aaa");
            _time.Advance(TimeSpan.FromDays(3));
            store.Record(repo, "latest", "sha256:aaa");
            _time.Advance(TimeSpan.FromDays(4));
            store.Record(repo, "latest", "sha256:bbb");
            store.Record(repo, "stable", "sha256:ccc");

            var reloaded = new DigestHistoryStore(path, _time).Get(repo, "latest");

            Assert.HasCount(2, reloaded);
            Assert.AreEqual("sha256:bbb", reloaded[0].Digest);
            Assert.AreEqual("sha256:aaa", reloaded[1].Digest);
            Assert.AreEqual(Now, reloaded[1].FirstSeen);
            Assert.AreEqual(Now.AddDays(3), reloaded[1].LastSeen);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- History service ----

    [TestMethod]
    public async Task HistoryListsReleasesNewestFirstWithLocalAndUserImages()
    {
        var gone = _registry.AddImage(Now.AddDays(-200), salt: "gone");
        var jan = _registry.AddImage(Now.AddDays(-100), salt: "jan");
        var feb = _registry.AddImage(Now.AddDays(-70), salt: "feb");
        var mar = _registry.AddImage(Now.AddDays(-40), salt: "mar");
        _registry.Tag("cosmosdb/emulator", "vnext-EN20260101", jan.IndexDigest);
        _registry.Tag("cosmosdb/emulator", "vnext-EN20260201", feb.IndexDigest);
        _registry.Tag("cosmosdb/emulator", "vnext-EN20260301", mar.IndexDigest);
        _registry.Tag("cosmosdb/emulator", "vnext-EN20260301-amd64", mar.Amd64Digest);
        _registry.Tag("cosmosdb/emulator", "vnext-latest", mar.IndexDigest);

        // The app saw the tag on an image that no tag points to any more.
        _store.Record(ImageReference.Parse(Repo), "vnext-latest", gone.IndexDigest);
        _time.Advance(TimeSpan.FromDays(1));

        var requested = await new ImageInspector(new RegistryClient(new HttpClient(_registry), new CredentialChain([]), new MemoryContentCache()))
            .InspectAsync(ImageReference.Parse(Repo), feb.IndexDigest, Platform.LinuxAmd64);

        var history = await _history.GetAsync(ImageReference.Parse($"{Repo}:vnext-latest"), Platform.LinuxAmd64, requested);

        CollectionAssert.AreEqual(new[] { mar.IndexDigest, feb.IndexDigest, jan.IndexDigest }, history.Releases.Select(r => r.Digest).ToArray());
        Assert.AreEqual(mar.IndexDigest, history.CurrentDigest);
        Assert.IsTrue(history.Releases[0].IsCurrent);
        CollectionAssert.Contains(history.Releases[0].Tags.ToArray(), "vnext-latest");
        Assert.AreEqual("vnext-EN20260301", history.Releases[0].Label);
        Assert.IsNotNull(history.Releases[0].Seen, "The current digest is recorded when the history is opened.");
        Assert.IsTrue(history.Releases[1].IsUserImage);
        Assert.AreEqual(gone.IndexDigest, history.Untagged.Single().Digest);
        Assert.AreEqual("dated tags like vnext-EN20260301", history.Source);
        Assert.IsFalse(history.DetailsCostPulls);

        var details = await _history.LoadDetailsAsync(history, history.Releases[2]);
        Assert.AreEqual(jan.Created, details.Created);
        Assert.AreEqual(jan.Amd64Digest, details.ManifestDigest);
        Assert.AreEqual($"{Repo}:vnext-EN20260101@{jan.Amd64Digest}", details.Reference.ToString());
    }

    [TestMethod]
    public async Task TagWithoutReleaseTagsShowsCurrentAndRecordedDigests()
    {
        var old = _registry.AddImage(Now.AddDays(-30), salt: "old");
        var current = _registry.AddImage(Now.AddDays(-1), salt: "new");
        _registry.Tag("cosmosdb/emulator", "nightly", old.IndexDigest);
        await _history.GetAsync(ImageReference.Parse($"{Repo}:nightly"), Platform.LinuxAmd64);

        _time.Advance(TimeSpan.FromDays(10));
        _registry.Tag("cosmosdb/emulator", "nightly", current.IndexDigest);
        var history = await _history.GetAsync(ImageReference.Parse($"{Repo}:nightly"), Platform.LinuxAmd64);

        Assert.IsNull(history.Source);
        Assert.AreEqual(current.IndexDigest, history.Releases.Single().Digest);
        Assert.AreEqual(old.IndexDigest, history.Untagged.Single().Digest);
        Assert.AreEqual(Now, history.Untagged.Single().Seen?.FirstSeen);
    }

    [TestMethod]
    public async Task DockerHubHistoryUsesTagApiWithoutManifestDownloads()
    {
        var older = _registry.AddImage(Now.AddDays(-30), salt: "older");
        var newer = _registry.AddImage(Now.AddDays(-2), salt: "newer");
        _registry.Tag("library/nginx", "1.27.1", older.IndexDigest);
        _registry.Tag("library/nginx", "1.27.2", newer.IndexDigest);
        _registry.Tag("library/nginx", "1.27", newer.IndexDigest);
        _registry.AddHubResponse(
            "https://hub.docker.com/v2/namespaces/library/repositories/nginx/tags?page_size=100&ordering=last_updated",
            JsonSerializer.Serialize(new { next = (string?)null, results = new[] { HubTag("1.27", newer, 300), HubTag("1.27.2", newer, 300), HubTag("1.27.1", older, 200) } }));

        var history = await _history.GetAsync(ImageReference.Parse("nginx:1.27"), Platform.LinuxAmd64);

        CollectionAssert.AreEqual(new[] { newer.IndexDigest, older.IndexDigest }, history.Releases.Select(r => r.Digest).ToArray());
        Assert.AreEqual(300, history.Releases[0].ListedSize);
        Assert.AreEqual(older.Created, history.Releases[1].Pushed);
        Assert.IsTrue(history.DetailsCostPulls);
        Assert.AreEqual(0, _registry.ManifestGets, "The list itself must not cost Docker Hub pulls.");
    }

    [TestMethod]
    public async Task AnalysisRecordsWhereTheTagPoints()
    {
        var old = _registry.AddImage(Now.AddDays(-30), salt: "old");
        var current = _registry.AddImage(Now.AddDays(-1), salt: "new");
        _registry.Tag("cosmosdb/emulator", "nightly", current.IndexDigest);

        _history.RecordAnalysis(await _analyzer.AnalyzeAsync($"{Repo}:nightly@{old.IndexDigest}", new AnalysisOptions()));

        Assert.AreEqual(current.IndexDigest, _store.Get(ImageReference.Parse(Repo), "nightly").Single().Digest);
    }

    private static object HubTag(string name, FakeImage img, long amd64Size) => new
    {
        name,
        digest = img.IndexDigest,
        tag_last_pushed = img.Created.ToString("O"),
        images = new object[]
        {
            new { os = "linux", architecture = "amd64", digest = img.Amd64Digest, size = amd64Size },
            new { os = "linux", architecture = "arm64", variant = "v8", digest = img.Arm64Digest, size = 1L },
        },
    };
}
