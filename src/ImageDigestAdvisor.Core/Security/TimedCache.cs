using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ImageDigestAdvisor.Core.Security;

/// <summary>Cache for data that changes over time (vulnerability databases); entries expire.</summary>
public interface ITimedCache
{
    Task<byte[]?> GetAsync(string key, TimeSpan maxAge, CancellationToken ct = default);

    Task SetAsync(string key, byte[] content, CancellationToken ct = default);
}

public sealed class MemoryTimedCache(TimeProvider? time = null) : ITimedCache
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, byte[] Content)> _items = new(StringComparer.Ordinal);

    public Task<byte[]?> GetAsync(string key, TimeSpan maxAge, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(key, out var e) && _time.GetUtcNow() - e.At <= maxAge ? e.Content : null);

    public Task SetAsync(string key, byte[] content, CancellationToken ct = default)
    {
        _items[key] = (_time.GetUtcNow(), content);
        return Task.CompletedTask;
    }
}

/// <summary>Stores entries as files named by the SHA-256 of the key; the file time is the entry's age.</summary>
public sealed class FileTimedCache(string directory) : ITimedCache
{
    public string Directory { get; } = directory;

    public async Task<byte[]?> GetAsync(string key, TimeSpan maxAge, CancellationToken ct = default)
    {
        var path = PathFor(key);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > maxAge) return null;
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SetAsync(string key, byte[] content, CancellationToken ct = default)
    {
        var path = PathFor(key);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, content, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimization only.
        }
    }

    private string PathFor(string key) =>
        Path.Combine(Directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
}
