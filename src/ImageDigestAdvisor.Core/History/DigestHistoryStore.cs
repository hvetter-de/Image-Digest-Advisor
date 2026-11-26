using System.Text.Json;
using ImageDigestAdvisor.Core.References;

namespace ImageDigestAdvisor.Core.History;

/// <summary>One digest a tag pointed to while the app was watching.</summary>
public sealed record DigestObservation(string Digest, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

/// <summary>
/// Remembers which digest a tag pointed to each time the app resolved it. This is the only way to get
/// a history for tags that have no dated copies (e.g. <c>nginx:latest</c>), because registries keep none.
/// Stored as a small JSON file; without a path it only lives in memory (tests).
/// </summary>
public sealed class DigestHistoryStore(string? filePath = null, TimeProvider? timeProvider = null)
{
    /// <summary>Oldest observations are dropped beyond this many.</summary>
    public const int MaxObservations = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private List<StoredObservation>? _items;

    public static string DefaultPath() => Path.Combine(AppPaths.DataDirectory, "digest-history.json");

    /// <summary>Records that <paramref name="tag"/> points to <paramref name="digest"/> now.</summary>
    public void Record(ImageReference repo, string tag, string digest)
    {
        var key = Key(repo, tag);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var items = Load();
            var last = items.LastOrDefault(o => o.Key == key);
            if (last is not null && string.Equals(last.Digest, digest, StringComparison.OrdinalIgnoreCase))
            {
                if (now > last.LastSeen) last.LastSeen = now;
            }
            else
            {
                items.Add(new StoredObservation { Key = key, Digest = digest, FirstSeen = now, LastSeen = now });
                if (items.Count > MaxObservations) items.RemoveRange(0, items.Count - MaxObservations);
            }
            Save(items);
        }
    }

    /// <summary>Digests seen for <paramref name="tag"/>, newest first. A digest seen again later appears once per period.</summary>
    public IReadOnlyList<DigestObservation> Get(ImageReference repo, string tag)
    {
        var key = Key(repo, tag);
        lock (_gate)
        {
            return Load()
                .Where(o => o.Key == key)
                .OrderByDescending(o => o.LastSeen)
                .Select(o => new DigestObservation(o.Digest, o.FirstSeen, o.LastSeen))
                .ToList();
        }
    }

    private static string Key(ImageReference repo, string tag) => $"{repo.Registry}/{repo.Repository}:{tag}";

    private List<StoredObservation> Load()
    {
        if (_items is not null) return _items;
        _items = [];
        if (filePath is null || !File.Exists(filePath)) return _items;

        try
        {
            _items = JsonSerializer.Deserialize<List<StoredObservation>>(File.ReadAllText(filePath), JsonOptions) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged file only loses the history.
        }
        return _items;
    }

    private void Save(List<StoredObservation> items)
    {
        if (filePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var temp = filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(items, JsonOptions));
            File.Move(temp, filePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class StoredObservation
    {
        public string Key { get; set; } = string.Empty;

        public string Digest { get; set; } = string.Empty;

        public DateTimeOffset FirstSeen { get; set; }

        public DateTimeOffset LastSeen { get; set; }
    }
}
