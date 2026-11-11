namespace ImageDigestAdvisor.Core.Security;

/// <summary>
/// Approximate version comparison that works across Debian, Alpine, SemVer and PEP 440 versions well enough
/// to pick the right fix range: an optional epoch (<c>1:</c>), then runs of digits compared numerically and
/// other characters compared by character, where <c>~</c> sorts before everything (Debian pre-releases)
/// and a <c>-</c> / <c>+</c> suffix sorts after the bare version.
/// </summary>
public static class PackageVersion
{
    public static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        var (epochA, restA) = SplitEpoch(a.TrimStart('v', 'V'));
        var (epochB, restB) = SplitEpoch(b.TrimStart('v', 'V'));
        if (epochA != epochB) return epochA.CompareTo(epochB);

        int i = 0, j = 0;
        while (i < restA.Length || j < restB.Length)
        {
            var ca = i < restA.Length ? restA[i] : '\0';
            var cb = j < restB.Length ? restB[j] : '\0';

            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                var startA = i;
                var startB = j;
                while (i < restA.Length && char.IsDigit(restA[i])) i++;
                while (j < restB.Length && char.IsDigit(restB[j])) j++;
                var numA = restA[startA..i].TrimStart('0');
                var numB = restB[startB..j].TrimStart('0');
                if (numA.Length != numB.Length) return numA.Length.CompareTo(numB.Length);
                var cmp = string.CompareOrdinal(numA, numB);
                if (cmp != 0) return cmp;
                continue;
            }

            if (ca == cb)
            {
                i++;
                j++;
                continue;
            }
            return Weight(ca).CompareTo(Weight(cb));
        }
        return 0;
    }

    /// <summary>'~' lowest, then end of string, then digits, then letters, then other separators.</summary>
    private static int Weight(char c) => c switch
    {
        '~' => -2,
        '\0' => -1,
        _ when char.IsDigit(c) => 0,
        _ when char.IsLetter(c) => 1 + char.ToLowerInvariant(c),
        _ => 1000 + c,
    };

    private static (long Epoch, string Remainder) SplitEpoch(string v)
    {
        var colon = v.IndexOf(':');
        return colon > 0 && long.TryParse(v.AsSpan(0, colon), out var epoch) ? (epoch, v[(colon + 1)..]) : (0, v);
    }
}
