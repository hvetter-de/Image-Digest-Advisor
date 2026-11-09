using ImageDigestAdvisor.Core.Analysis;
using ImageDigestAdvisor.Core.References;

namespace ImageDigestAdvisor.Core.Tests;

[TestClass]
public sealed class VersionTests
{
    [TestMethod]
    public void FamilyIgnoresDigitsInSuffix()
    {
        var a = TagVersion.TryParse("1.27.2-alpine3.20")!;
        var b = TagVersion.TryParse("1.27.3-alpine3.21")!;
        var c = TagVersion.TryParse("1.27.3-bookworm")!;
        var d = TagVersion.TryParse("1.27-alpine3.21")!;

        Assert.IsTrue(a.IsSameFamily(b));
        Assert.IsFalse(a.IsSameFamily(c));
        Assert.IsFalse(a.IsSameFamily(d), "Different precision must not be mixed.");
    }

    [TestMethod]
    [DataRow("latest")]
    [DataRow("mainline")]
    [DataRow("bookworm")]
    [DataRow("1.2.3.4.5")]
    public void NonVersionTagsAreNotParsed(string tag) => Assert.IsNull(TagVersion.TryParse(tag));

    [TestMethod]
    public void SuffixNumbersBreakTies()
    {
        var older = TagVersion.TryParse("1.27.3-alpine3.20")!;
        var newer = TagVersion.TryParse("1.27.3-alpine3.21")!;
        Assert.IsTrue(newer.CompareTo(older) > 0);
    }

    [TestMethod]
    public void FindNewerReturnsPatchMinorAndMajor()
    {
        var tags = new[] { "1.26.0", "1.27.1", "1.27.2", "1.27.4", "1.27.10", "1.28.0", "1.29.3", "2.0.1", "2.0.1-alpine", "1.27", "latest", "1.30.0-rc1" };

        var result = VersionMath.FindNewer("1.27.2", tags);

        CollectionAssert.AreEqual(
            new[] { (SuggestionKind.NewestPatch, "1.27.10"), (SuggestionKind.NewestMinor, "1.29.3"), (SuggestionKind.NewestMajor, "2.0.1") },
            result.ToArray());
    }

    [TestMethod]
    public void FindNewerStaysInVariant()
    {
        var tags = new[] { "8.0.10-bookworm-slim", "8.0.11-bookworm-slim", "8.0.12", "8.0.12-alpine" };
        var result = VersionMath.FindNewer("8.0.10-bookworm-slim", tags);

        Assert.HasCount(1, result);
        Assert.AreEqual((SuggestionKind.NewestPatch, "8.0.11-bookworm-slim"), result[0]);
    }

    [TestMethod]
    public void FindNewerForTwoPartTagHasNoPatch()
    {
        var result = VersionMath.FindNewer("8.0", ["8.0", "9.0", "10.0", "8.0.1"]);

        Assert.HasCount(1, result);
        Assert.AreEqual((SuggestionKind.NewestMajor, "10.0"), result[0]);
    }

    [TestMethod]
    public void FindNewerForNonVersionTagIsEmpty() => Assert.IsEmpty(VersionMath.FindNewer("latest", ["1.0", "2.0"]));

    [TestMethod]
    public void PickBestTagPrefersMostSpecificVersion()
    {
        Assert.AreEqual("1.27.2", VersionMath.PickBestTag(["latest", "1", "1.27", "mainline", "1.27.2-bookworm", "1.27.2"]));
        Assert.AreEqual("stable", VersionMath.PickBestTag(["latest", "stable"]));
        Assert.IsNull(VersionMath.PickBestTag([]));
    }

    [TestMethod]
    public void HintCandidatesRespectKnownTags()
    {
        CollectionAssert.AreEqual(new[] { "1.27.2" }, VersionMath.HintCandidates(["9.9", "1.27.2"], ["1.27.2", "latest"], tagListComplete: true).ToArray());
        CollectionAssert.AreEqual(new[] { "v2.1.0" }, VersionMath.HintCandidates(["2.1.0"], ["v2.1.0"], tagListComplete: true).ToArray());
        Assert.IsEmpty(VersionMath.HintCandidates(["3.0"], ["1.0"], tagListComplete: true));
        CollectionAssert.AreEqual(new[] { "3.0", "v3.0" }, VersionMath.HintCandidates(["3.0", "not-a-version"], ["1.0"], tagListComplete: false).ToArray());
    }

    [TestMethod]
    public void PlatformMatchingTreatsArm64V8AsArm64()
    {
        Assert.IsTrue(Platform.Parse("linux/arm64/v8").Matches(Platform.Parse("linux/arm64")));
        Assert.IsFalse(Platform.Parse("linux/arm/v7").Matches(Platform.Parse("linux/arm/v6")));
        Assert.ThrowsExactly<FormatException>(() => Platform.Parse("linux"));
    }
}
