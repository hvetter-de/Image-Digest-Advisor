using System.Text.RegularExpressions;
using ImageDigestAdvisor.Core.References;

namespace ImageDigestAdvisor.Core.History;

/// <summary>Release tags that record where a moving tag pointed in the past, newest first.</summary>
/// <param name="Description">E.g. "dated tags like vnext-EN20261123".</param>
public sealed record TagFamilyMatch(IReadOnlyList<string> Tags, string? Description);

/// <summary>
/// Registries don't keep a history of moving tags such as <c>latest</c>. Many publishers push a frozen
/// copy of every release under its own tag, though: <c>vnext-latest</c> next to <c>vnext-EN20261123</c>,
/// <c>1.27</c> next to <c>1.27.0</c>, <c>1.27.1</c>, … This class finds those tags.
/// </summary>
public static partial class TagFamily
{
    /// <summary>Finds the release tags of <paramref name="tag"/> among <paramref name="allTags"/>, newest first.</summary>
    public static TagFamilyMatch Find(string tag, IEnumerable<string> allTags)
    {
        var tags = allTags.Where(t => !string.Equals(t, tag, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();

        // 1. A version tag like "1.27" or "8.0-alpine": its more specific versions with the same variant.
        if (TagVersion.TryParse(tag) is { } version)
        {
            var variant = NormalizeVariant(version.Suffix);
            var refined = tags
                .Select(TagVersion.TryParse)
                .OfType<TagVersion>()
                .Where(v => string.Equals(v.Prefix, version.Prefix, StringComparison.OrdinalIgnoreCase)
                            && v.Parts.Count > version.Parts.Count
                            && v.Parts.Take(version.Parts.Count).SequenceEqual(version.Parts)
                            && NormalizeVariant(v.Suffix) == variant)
                .Select(v => v.Tag)
                .ToList();
            return refined.Count == 0
                ? new TagFamilyMatch([], null)
                : new TagFamilyMatch(Newest(refined), $"version tags {tag}.x");
        }

        // 2. A channel tag like "latest" or "vnext-latest": "vnext-" + a release stamp (date or version).
        var stem = ChannelWord().Match(tag) is { Success: true } m ? m.Groups["stem"].Value : tag + "-";
        var releases = tags
            .Where(t => t.StartsWith(stem, StringComparison.Ordinal) && ReleaseStamp().IsMatch(t[stem.Length..]))
            .ToList();
        if (releases.Count == 0) return new TagFamilyMatch([], null);

        var newest = Newest(releases);
        var kind = newest.All(t => DateStamp().IsMatch(t[stem.Length..])) ? "dated tags" : "release tags";
        return new TagFamilyMatch(newest, $"{kind} like {newest[0]}");
    }

    /// <summary>Orders tags by the numbers they contain, newest (highest) first: 1.27.10 before 1.27.9, EN20250414-2 before EN20250414.</summary>
    public static IReadOnlyList<string> Newest(IEnumerable<string> tags) =>
        tags.OrderByDescending(SortKey, NumberListComparer.Instance).ThenByDescending(t => t, StringComparer.Ordinal).ToList();

    private static IReadOnlyList<long> SortKey(string tag) =>
        Numbers().Matches(tag).Select(m => long.TryParse(m.Value, out var n) ? n : long.MaxValue).ToList();

    /// <summary>"-alpine3.20" and "-alpine3.21" are the same variant; "-alpine" is not.</summary>
    private static string NormalizeVariant(string suffix) => Numbers().Replace(suffix, "#");

    private sealed class NumberListComparer : IComparer<IReadOnlyList<long>>
    {
        public static readonly NumberListComparer Instance = new();

        public int Compare(IReadOnlyList<long>? x, IReadOnlyList<long>? y)
        {
            x ??= [];
            y ??= [];
            for (var i = 0; i < Math.Min(x.Count, y.Count); i++)
            {
                if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            }
            return x.Count.CompareTo(y.Count);
        }
    }

    // "latest", "vnext-latest", "stable", "lts" → stem "", "vnext-", …
    [GeneratedRegex(@"^(?<stem>.*?)(?:latest|stable|current|lts)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelWord();

    // A release stamp: optional short letter prefix, then a date or version, optionally a short build counter.
    // Matches "EN20261123", "EN20250414-2", "2.14.24", "20240101"; not "2.14.24-7610c980", "EN20250117-amd64", "preview".
    [GeneratedRegex(@"^[A-Za-z]{0,4}[-_.]?\d+(?:[._]\d+)*(?:-\d{1,3})?$")]
    private static partial Regex ReleaseStamp();

    [GeneratedRegex(@"^[A-Za-z]{0,4}[-_.]?(?:19|20)\d{6}(?:-\d{1,3})?$")]
    private static partial Regex DateStamp();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();
}
