using ImageDigestAdvisor.Core.References;

namespace ImageDigestAdvisor.Core.Analysis;

/// <summary>Pure tag logic: picking the most meaningful tag and finding newer versions.</summary>
public static class VersionMath
{
    /// <summary>
    /// Finds newer tags in the same family as <paramref name="currentTag"/>:
    /// newest patch (same major.minor), newest minor (same major) and newest major.
    /// </summary>
    public static IReadOnlyList<(SuggestionKind Kind, string Tag)> FindNewer(string currentTag, IEnumerable<string> tags)
    {
        var current = TagVersion.TryParse(currentTag);
        if (current is null) return [];

        var newer = tags
            .Select(TagVersion.TryParse)
            .OfType<TagVersion>()
            .Where(v => v.IsSameFamily(current) && v.CompareTo(current) > 0)
            .ToList();

        var result = new List<(SuggestionKind, string)>();

        if (current.Parts.Count >= 3 && Max(newer.Where(v => v.Major == current.Major && v.Minor == current.Minor)) is { } patch)
        {
            result.Add((SuggestionKind.NewestPatch, patch.Tag));
        }

        if (current.Parts.Count >= 2 && Max(newer.Where(v => v.Major == current.Major && v.Minor > current.Minor)) is { } minor)
        {
            result.Add((SuggestionKind.NewestMinor, minor.Tag));
        }

        if (Max(newer.Where(v => v.Major > current.Major)) is { } major)
        {
            result.Add((SuggestionKind.NewestMajor, major.Tag));
        }

        return result;
    }

    /// <summary>
    /// From all tags pointing at the same image, picks the most specific one:
    /// <c>1.27.2</c> beats <c>1.27</c>, <c>1</c>, <c>mainline</c> and <c>latest</c>; no suffix beats a suffix.
    /// </summary>
    public static string? PickBestTag(IEnumerable<string> tags)
    {
        var list = tags.Distinct().ToList();
        var versioned = list
            .Select(TagVersion.TryParse)
            .OfType<TagVersion>()
            .OrderByDescending(v => v.Parts.Count)
            .ThenBy(v => v.Suffix.Length)
            .ThenBy(v => v.Tag.Length)
            .ThenBy(v => v.Tag, StringComparer.Ordinal)
            .FirstOrDefault();

        if (versioned is not null) return versioned.Tag;

        return list
            .OrderBy(t => t == "latest" ? 1 : 0)
            .ThenBy(t => t.Length)
            .ThenBy(t => t, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// Turns version hints (e.g. <c>1.27.2</c> from <c>NGINX_VERSION</c>) into tag candidates, with and
    /// without a <c>v</c> prefix. If the tag list is complete, only existing tags are returned.
    /// </summary>
    public static IReadOnlyList<string> HintCandidates(IEnumerable<string> hints, IReadOnlyCollection<string>? knownTags, bool tagListComplete)
    {
        var candidates = hints
            .SelectMany(h => new[] { h, h.StartsWith('v') ? h[1..] : "v" + h })
            .Where(c => c.Length > 0 && TagVersion.TryParse(c) is not null)
            .Distinct(StringComparer.Ordinal);

        if (knownTags is not null)
        {
            var set = new HashSet<string>(knownTags, StringComparer.Ordinal);
            // Known tags first; unknown ones only make sense if the list is incomplete.
            candidates = candidates.OrderByDescending(set.Contains).Where(c => set.Contains(c) || !tagListComplete);
        }

        return candidates.Take(6).ToList();
    }

    private static TagVersion? Max(IEnumerable<TagVersion> versions) =>
        versions.Aggregate((TagVersion?)null, (best, v) => best is null || v.CompareTo(best) > 0 ? v : best);
}
