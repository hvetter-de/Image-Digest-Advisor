using System.Text.RegularExpressions;

namespace ImageDigestAdvisor.Core.References;

/// <summary>
/// Interprets a tag such as <c>1.27.2-alpine3.20</c> as a version (<c>1.27.2</c>) plus a variant
/// (<c>-alpine3.20</c>). Tags are only compared within the same <see cref="Family"/>: same prefix,
/// same number of version parts and the same variant with digits ignored. That way
/// <c>1.27.2-alpine3.20</c> can be upgraded to <c>1.27.3-alpine3.21</c> but never to <c>1.28-bookworm</c>.
/// </summary>
public sealed partial record TagVersion : IComparable<TagVersion>
{
    public required string Tag { get; init; }

    /// <summary>Optional prefix such as <c>v</c>.</summary>
    public required string Prefix { get; init; }

    public required IReadOnlyList<long> Parts { get; init; }

    /// <summary>Everything after the version, e.g. <c>-alpine3.20</c> (empty if none).</summary>
    public required string Suffix { get; init; }

    /// <summary>Numbers found inside the suffix, used as a tiebreaker (alpine3.21 &gt; alpine3.20).</summary>
    public required IReadOnlyList<long> SuffixNumbers { get; init; }

    /// <summary>Key used to decide whether two tags are comparable.</summary>
    public string Family => $"{Prefix}|{Parts.Count}|{SuffixDigits().Replace(Suffix, "#")}";

    public long Major => Parts[0];

    public long? Minor => Parts.Count > 1 ? Parts[1] : null;

    public static TagVersion? TryParse(string tag)
    {
        var match = TagRegex().Match(tag);
        if (!match.Success) return null;

        var parts = match.Groups["ver"].Value.Split('.').Select(long.Parse).ToArray();
        var suffix = match.Groups["suffix"].Value;
        var suffixNumbers = SuffixDigits().Matches(suffix)
            .SelectMany(m => m.Value.Split('.', StringSplitOptions.RemoveEmptyEntries))
            .Select(s => long.TryParse(s, out var n) ? n : 0)
            .ToArray();

        return new TagVersion
        {
            Tag = tag,
            Prefix = match.Groups["prefix"].Value,
            Parts = parts,
            Suffix = suffix,
            SuffixNumbers = suffixNumbers,
        };
    }

    public bool IsSameFamily(TagVersion other) => Family == other.Family;

    public int CompareTo(TagVersion? other)
    {
        if (other is null) return 1;
        var c = CompareLists(Parts, other.Parts);
        return c != 0 ? c : CompareLists(SuffixNumbers, other.SuffixNumbers);
    }

    private static int CompareLists(IReadOnlyList<long> a, IReadOnlyList<long> b)
    {
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    // Version: 1 to 4 numeric parts. Suffix must start with '-', '_' or '+' so that tags like "2024abc" are rejected.
    [GeneratedRegex(@"^(?<prefix>v?)(?<ver>\d{1,9}(?:\.\d{1,9}){0,3})(?<suffix>[-_+].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\d+(?:\.\d+)*")]
    private static partial Regex SuffixDigits();
}
