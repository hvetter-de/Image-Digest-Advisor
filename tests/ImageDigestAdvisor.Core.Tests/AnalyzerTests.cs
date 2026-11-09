using System.Text.Json;
using ImageDigestAdvisor.Core.Analysis;
using ImageDigestAdvisor.Core.Credentials;
using ImageDigestAdvisor.Core.References;
using ImageDigestAdvisor.Core.Registry;
using ImageDigestAdvisor.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace ImageDigestAdvisor.Core.Tests;

[TestClass]
public sealed class AnalyzerTests
{
    private const string Repo = "registry.example.com/team/web";
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private FakeRegistry _registry = null!;
    private InMemoryCredentialStore _credentials = null!;
    private DigestAnalyzer _analyzer = null!;
    private RegistryClient _client = null!;

    [TestInitialize]
    public void Init()
    {
        _registry = new FakeRegistry();
        _credentials = new InMemoryCredentialStore();
        var http = new HttpClient(_registry);
        _client = new RegistryClient(http, new CredentialChain([_credentials]), new MemoryContentCache());
        var catalog = new TagCatalog(http, _client);
        TagCatalog.CacheDuration = TimeSpan.Zero;
        _analyzer = new DigestAnalyzer(_client, catalog, new ImageInspector(_client), new FakeTimeProvider(Now));
    }

    private static AnalysisOptions Options(int? maxAge = 90) => new() { MaxAgeDays = maxAge };

    [TestMethod]
    public async Task DigestThatIsStillCurrentIsUpToDate()
    {
        var img = _registry.AddImage(Now.AddDays(-5), labels: new Dictionary<string, string> { ["org.opencontainers.image.version"] = "1.4.2" });
        _registry.Tag("team/web", "1.4.2", img.IndexDigest);
        _registry.Tag("team/web", "1.4", img.IndexDigest);

        var result = await _analyzer.AnalyzeAsync($"{Repo}:1.4.2@{img.IndexDigest}", Options());

        Assert.AreEqual(OverallStatus.UpToDate, result.Status);
        Assert.IsEmpty(result.Suggestions);
        Assert.AreEqual(img.Amd64Digest, result.Requested.ManifestDigest);
        Assert.AreEqual(img.IndexDigest, result.Requested.IndexDigest);
        Assert.AreEqual(Now.AddDays(-5), result.Requested.Created);
        Assert.AreEqual("1.4.2", result.Requested.Label("org.opencontainers.image.version"));
        Assert.HasCount(2, result.Requested.AvailablePlatforms, "Attestation entries must be ignored.");
        Assert.AreEqual(1, result.Requested.LayerCount);
    }

    [TestMethod]
    public async Task MovedTagIsOutdatedAndSuggestsCurrentDigest()
    {
        var old = _registry.AddImage(Now.AddDays(-40), salt: "old");
        var rebuilt = _registry.AddImage(Now.AddDays(-2), salt: "new");
        _registry.Tag("team/web", "1.4", rebuilt.IndexDigest);

        var result = await _analyzer.AnalyzeAsync($"{Repo}:1.4@{old.IndexDigest}", Options());

        Assert.AreEqual(OverallStatus.Outdated, result.Status);
        Assert.IsTrue(result.Findings.Any(f => f.Kind == FindingKind.TagMoved));
        var s = result.Suggestions.Single();
        Assert.AreEqual(SuggestionKind.SameTag, s.Kind);
        Assert.AreEqual($"{Repo}:1.4@{rebuilt.IndexDigest}", s.PinnedReference);
        Assert.AreEqual(Now.AddDays(-2), s.Details.Created);
    }

    [TestMethod]
    public async Task NewerPatchAndMinorAreSuggestedAndDeduplicated()
    {
        var current = _registry.AddImage(Now.AddDays(-10), salt: "a");
        var patch = _registry.AddImage(Now.AddDays(-3), salt: "b");
        var minor = _registry.AddImage(Now.AddDays(-1), salt: "c");
        _registry.Tag("team/web", "1.4.2", current.IndexDigest);
        _registry.Tag("team/web", "1.4.5", patch.IndexDigest);
        _registry.Tag("team/web", "1.5.0", minor.IndexDigest);
        _registry.Tag("team/web", "1.5.0-debug", minor.IndexDigest);

        var result = await _analyzer.AnalyzeAsync($"{Repo}:1.4.2@{current.IndexDigest}", Options());

        Assert.AreEqual(OverallStatus.Outdated, result.Status);
        CollectionAssert.AreEqual(new[] { "1.4.5", "1.5.0" }, result.Suggestions.Select(s => s.Tag).ToArray());
        CollectionAssert.AreEqual(new[] { SuggestionKind.NewestPatch, SuggestionKind.NewestMinor }, result.Suggestions.Select(s => s.Kind).ToArray());
        Assert.IsTrue(result.Findings.Any(f => f.Kind == FindingKind.UpToDate), "Same tag did not move.");
    }

    [TestMethod]
    public async Task OldImageIsFlaggedByAgeThreshold()
    {
        var img = _registry.AddImage(Now.AddDays(-200));
        _registry.Tag("team/web", "stable", img.IndexDigest);

        var flagged = await _analyzer.AnalyzeAsync($"{Repo}:stable@{img.IndexDigest}", Options(90));
        var notFlagged = await _analyzer.AnalyzeAsync($"{Repo}:stable@{img.IndexDigest}", Options(null));

        Assert.AreEqual(OverallStatus.Outdated, flagged.Status);
        Assert.IsTrue(flagged.Findings.Any(f => f.Kind == FindingKind.OlderThanThreshold));
        Assert.AreEqual(OverallStatus.UpToDate, notFlagged.Status);
    }

    [TestMethod]
    public async Task DigestWithoutTagIsFoundByProbingTags()
    {
        var img = _registry.AddImage(Now.AddDays(-1));
        _registry.Tag("team/web", "latest", img.IndexDigest);
        _registry.Tag("team/web", "2.0.1", img.IndexDigest);
        _registry.Tag("team/web", "2.0", img.IndexDigest);

        var result = await _analyzer.AnalyzeAsync($"{Repo}@{img.IndexDigest}", Options());

        Assert.AreEqual("2.0.1", result.EffectiveTag);
        Assert.IsFalse(result.TagWasInferred);
        CollectionAssert.AreEquivalent(new[] { "latest", "2.0.1", "2.0" }, result.Requested.Tags.ToArray());
        Assert.AreEqual(OverallStatus.UpToDate, result.Status);
    }

    [TestMethod]
    public async Task UntaggedDigestTagIsInferredFromEnvironment()
    {
        var old = _registry.AddImage(Now.AddDays(-30), env: new Dictionary<string, string> { ["WEB_VERSION"] = "3.1.0" }, salt: "old");
        var rebuilt = _registry.AddImage(Now.AddDays(-1), env: new Dictionary<string, string> { ["WEB_VERSION"] = "3.1.0" }, salt: "new");
        _registry.Tag("team/web", "3.1.0", rebuilt.IndexDigest);

        var result = await _analyzer.AnalyzeAsync($"{Repo}@{old.IndexDigest}", Options());

        Assert.AreEqual("3.1.0", result.EffectiveTag);
        Assert.IsTrue(result.TagWasInferred);
        Assert.AreEqual(OverallStatus.Outdated, result.Status);
        Assert.AreEqual(rebuilt.IndexDigest, result.Suggestions.Single().Details.PinnableDigest);
    }

    [TestMethod]
    public async Task PlatformSpecificDigestUsesItsOwnPlatform()
    {
        var img = _registry.AddImage(Now.AddDays(-1));
        _registry.Tag("team/web", "1.0.0", img.IndexDigest);

        var result = await _analyzer.AnalyzeAsync($"{Repo}:1.0.0@{img.Arm64Digest}", Options());

        Assert.AreEqual("arm64", result.Requested.Platform.Architecture);
        Assert.AreEqual(OverallStatus.UpToDate, result.Status, "The arm64 manifest is part of the current index.");
    }

    [TestMethod]
    public async Task TagOnlyInputShowsCurrentDigestToPin()
    {
        var img = _registry.AddImage(Now.AddDays(-1));
        _registry.Tag("team/web", "latest", img.IndexDigest);

        var result = await _analyzer.AnalyzeAsync($"{Repo}:latest", Options());

        Assert.AreEqual(img.IndexDigest, result.Requested.PinnableDigest);
        Assert.AreEqual(OverallStatus.UpToDate, result.Status);
    }

    [TestMethod]
    public async Task UnknownDigestThrowsNotFound()
    {
        var e = await Assert.ThrowsExactlyAsync<RegistryException>(() =>
            _analyzer.AnalyzeAsync($"{Repo}@sha256:{new string('a', 64)}", Options()));
        Assert.AreEqual(RegistryErrorKind.NotFound, e.Kind);
    }

    [TestMethod]
    public async Task PrivateRegistryWithoutLoginGivesHelpfulError()
    {
        _registry.RequireLogin = true;
        var img = _registry.AddImage(Now);
        _registry.Tag("team/web", "1.0.0", img.IndexDigest);

        var e = await Assert.ThrowsExactlyAsync<RegistryException>(() => _analyzer.AnalyzeAsync($"{Repo}:1.0.0", Options()));

        Assert.AreEqual(RegistryErrorKind.Unauthorized, e.Kind);
        StringAssert.Contains(e.Message, "Registry accounts");
    }

    [TestMethod]
    public async Task SavedAccountIsUsedForPrivateRegistry()
    {
        _registry.RequireLogin = true;
        _registry.ValidLogin = ("me", "token");
        _credentials.Save("registry.example.com", "me", "token");
        var img = _registry.AddImage(Now);
        _registry.Tag("team/web", "1.0.0", img.IndexDigest);

        var result = await _analyzer.AnalyzeAsync($"{Repo}:1.0.0", Options());

        Assert.AreEqual(img.IndexDigest, result.Requested.IndexDigest);
    }

    [TestMethod]
    public async Task WrongSavedCredentialsFallBackToAnonymousForPublicImages()
    {
        _registry.ValidLogin = ("me", "right");
        _credentials.Save("registry.example.com", "me", "wrong");
        var img = _registry.AddImage(Now);
        _registry.Tag("team/web", "1.0.0", img.IndexDigest);

        var result = await _analyzer.AnalyzeAsync($"{Repo}:1.0.0", Options());

        Assert.AreEqual(img.IndexDigest, result.Requested.IndexDigest);
    }

    [TestMethod]
    public async Task SecondAnalysisIsServedFromCache()
    {
        var img = _registry.AddImage(Now.AddDays(-1));
        _registry.Tag("team/web", "1.0.0", img.IndexDigest);
        var input = $"{Repo}:1.0.0@{img.IndexDigest}";

        await _analyzer.AnalyzeAsync(input, Options());
        var getsAfterFirst = _registry.ManifestGets;
        await _analyzer.AnalyzeAsync(input, Options());

        Assert.AreEqual(getsAfterFirst, _registry.ManifestGets, "Manifests by digest are immutable and must come from the cache.");
    }

    [TestMethod]
    public async Task CredentialsTestReportsSuccessAndFailure()
    {
        _registry.ValidLogin = ("me", "right");

        Assert.IsTrue((await _client.TestCredentialsAsync("registry.example.com", "me", "right")).Success);
        var wrong = await _client.TestCredentialsAsync("registry.example.com", "me", "wrong");
        Assert.IsFalse(wrong.Success);
        StringAssert.Contains(wrong.Message, "rejected");
    }

    [TestMethod]
    public async Task DockerHubUsesHubApiForTagDigestsAndReadsRateLimit()
    {
        var current = _registry.AddImage(Now.AddDays(-20), salt: "cur");
        var patch = _registry.AddImage(Now.AddDays(-1), salt: "patch");
        _registry.Tag("library/nginx", "1.27.2", current.IndexDigest);
        _registry.Tag("library/nginx", "1.27.3", patch.IndexDigest);
        _registry.RateLimitRemaining = "7;w=21600";

        // Old digest is no longer tagged on Hub; "1.27.3" is the newest patch.
        var old = _registry.AddImage(Now.AddDays(-60), env: new Dictionary<string, string> { ["NGINX_VERSION"] = "1.27.2" }, salt: "old");
        _registry.AddHubResponse(
            "https://hub.docker.com/v2/namespaces/library/repositories/nginx/tags?page_size=100&ordering=last_updated",
            JsonSerializer.Serialize(new
            {
                next = (string?)null,
                results = new[]
                {
                    HubTag("1.27.3", patch),
                    HubTag("1.27.2", current),
                },
            }));

        // Docker Hub talks to registry-1.docker.io; route it through the fake as well.
        var result = await _analyzer.AnalyzeAsync($"nginx@{old.IndexDigest}", Options());

        Assert.AreEqual("1.27.2", result.EffectiveTag);
        Assert.IsTrue(result.TagWasInferred);
        Assert.IsTrue(result.Findings.Any(f => f.Kind == FindingKind.DigestNoLongerTagged && f.Severity == Severity.Warning));
        Assert.IsTrue(result.Findings.Any(f => f.Kind == FindingKind.TagMoved));
        CollectionAssert.AreEqual(new[] { "1.27.2", "1.27.3" }, result.Suggestions.Select(s => s.Tag).ToArray());
        Assert.AreEqual(7, result.RateLimit?.Remaining);
        Assert.AreEqual(100, result.RateLimit?.Limit);
        Assert.IsFalse(_registry.Requests.Any(r => r.Url.Contains("/tags/list", StringComparison.Ordinal)), "Hub API must be used instead of probing tags.");
    }

    [TestMethod]
    public async Task OldTagBeyondHubPagingLimitIsFoundViaVersionHint()
    {
        var old = _registry.AddImage(Now.AddDays(-400), env: new Dictionary<string, string> { ["NGINX_VERSION"] = "1.25.0" }, salt: "old");
        var newest = _registry.AddImage(Now.AddDays(-1), salt: "newest");
        _registry.Tag("library/nginx", "1.25.0", old.IndexDigest);
        _registry.Tag("library/nginx", "1.31.8", newest.IndexDigest);

        // Page 1 only has recent tags; page 2 is refused (Hub's anonymous paging limit).
        _registry.AddHubResponse(
            "https://hub.docker.com/v2/namespaces/library/repositories/nginx/tags?page_size=100&ordering=last_updated",
            JsonSerializer.Serialize(new { next = "https://hub.docker.com/v2/refused-page-2", results = new[] { HubTag("1.31.8", newest) } }));

        var result = await _analyzer.AnalyzeAsync($"nginx@{old.IndexDigest}", Options());

        Assert.AreEqual("1.25.0", result.EffectiveTag);
        Assert.IsFalse(result.TagWasInferred, "HEAD confirmed the tag still points at this digest.");
        CollectionAssert.Contains(result.Requested.Tags.ToArray(), "1.25.0");
        Assert.IsFalse(result.Findings.Any(f => f.Kind == FindingKind.DigestNoLongerTagged));
        Assert.AreEqual("1.31.8", result.Suggestions.Single(s => s.Kind == SuggestionKind.NewestMinor).Tag);
    }

    private static object HubTag(string name, FakeImage img) => new
    {
        name,
        digest = img.IndexDigest,
        tag_last_pushed = img.Created.ToString("O"),
        images = new[]
        {
            new { os = "linux", architecture = "amd64", variant = (string?)null, digest = img.Amd64Digest },
            new { os = "linux", architecture = "arm64", variant = (string?)"v8", digest = img.Arm64Digest },
            new { os = "unknown", architecture = "unknown", variant = (string?)null, digest = "sha256:" + new string('0', 64) },
        },
    };
}
