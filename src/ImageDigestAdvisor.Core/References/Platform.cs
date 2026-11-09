namespace ImageDigestAdvisor.Core.References;

/// <summary>An OS/architecture combination such as <c>linux/amd64</c> or <c>linux/arm64/v8</c>.</summary>
public sealed record Platform(string Os, string Architecture, string? Variant = null)
{
    public static Platform LinuxAmd64 { get; } = new("linux", "amd64");

    public static Platform Parse(string text)
    {
        var parts = text.Trim().ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            2 => new Platform(parts[0], parts[1]),
            3 => new Platform(parts[0], parts[1], parts[2]),
            _ => throw new FormatException($"'{text}' is not a valid platform. Expected os/arch[/variant], e.g. linux/amd64."),
        };
    }

    /// <summary>Compares platforms, treating <c>arm64</c> with and without variant <c>v8</c> as equal.</summary>
    public bool Matches(Platform other) =>
        string.Equals(Os, other.Os, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Architecture, other.Architecture, StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizedVariant, other.NormalizedVariant, StringComparison.OrdinalIgnoreCase);

    private string? NormalizedVariant =>
        Architecture == "arm64" && Variant == "v8" ? null : string.IsNullOrEmpty(Variant) ? null : Variant;

    public override string ToString() => Variant is null ? $"{Os}/{Architecture}" : $"{Os}/{Architecture}/{Variant}";
}
