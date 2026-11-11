namespace ImageDigestAdvisor.Core.Security;

/// <summary>A CVSS vector with its base score, if we can compute it (CVSS 3.x only).</summary>
public sealed record CvssScore(string Version, string Vector, double? Score)
{
    /// <summary>None / Low / Medium / High / Critical, or null if the score is unknown.</summary>
    public string? Rating => Score switch
    {
        null => null,
        0 => "None",
        < 4 => "Low",
        < 7 => "Medium",
        < 9 => "High",
        _ => "Critical",
    };

    /// <summary>Parses an OSV severity entry (<c>CVSS_V3</c>, <c>CVSS_V4</c>, …).</summary>
    public static CvssScore? FromOsv(string type, string vector) => type switch
    {
        "CVSS_V3" => new CvssScore(vector.StartsWith("CVSS:3.0", StringComparison.Ordinal) ? "3.0" : "3.1", vector, Cvss3.BaseScore(vector)),
        // CVSS 4.0 scores need FIRST's macro-vector lookup table; until that is bundled the vector is shown unscored.
        "CVSS_V4" => new CvssScore("4.0", vector, null),
        "CVSS_V2" => new CvssScore("2.0", vector, null),
        _ => null,
    };
}

/// <summary>CVSS v3.0/v3.1 base score, as specified in https://www.first.org/cvss/v3.1/specification-document.</summary>
public static class Cvss3
{
    public static double? BaseScore(string vector)
    {
        var metrics = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in vector.Split('/'))
        {
            var colon = part.IndexOf(':');
            if (colon > 0) metrics[part[..colon]] = part[(colon + 1)..];
        }

        if (!metrics.TryGetValue("S", out var scope) || scope is not ("U" or "C")) return null;
        var changed = scope == "C";

        double? av = Get(metrics, "AV") switch { "N" => 0.85, "A" => 0.62, "L" => 0.55, "P" => 0.2, _ => null };
        double? ac = Get(metrics, "AC") switch { "L" => 0.77, "H" => 0.44, _ => null };
        double? pr = Get(metrics, "PR") switch { "N" => 0.85, "L" => changed ? 0.68 : 0.62, "H" => changed ? 0.5 : 0.27, _ => null };
        double? ui = Get(metrics, "UI") switch { "N" => 0.85, "R" => 0.62, _ => null };
        var c = Cia(Get(metrics, "C"));
        var i = Cia(Get(metrics, "I"));
        var a = Cia(Get(metrics, "A"));
        if (av is null || ac is null || pr is null || ui is null || c is null || i is null || a is null) return null;

        var iss = 1 - (1 - c.Value) * (1 - i.Value) * (1 - a.Value);
        var impact = changed ? 7.52 * (iss - 0.029) - 3.25 * Math.Pow(iss - 0.02, 15) : 6.42 * iss;
        if (impact <= 0) return 0;

        var exploitability = 8.22 * av.Value * ac.Value * pr.Value * ui.Value;
        return changed
            ? Roundup(Math.Min(1.08 * (impact + exploitability), 10))
            : Roundup(Math.Min(impact + exploitability, 10));
    }

    private static string? Get(Dictionary<string, string> m, string key) => m.TryGetValue(key, out var v) ? v : null;

    private static double? Cia(string? value) => value switch { "H" => 0.56, "L" => 0.22, "N" => 0, _ => null };

    /// <summary>CVSS 3.1 Roundup: smallest number with one decimal that is ≥ the input, robust against floating-point noise.</summary>
    internal static double Roundup(double value)
    {
        var intInput = (long)Math.Round(value * 100000);
        return intInput % 10000 == 0 ? intInput / 100000.0 : (Math.Floor(intInput / 10000.0) + 1) / 10.0;
    }
}
