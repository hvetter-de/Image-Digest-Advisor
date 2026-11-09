using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ImageDigestAdvisor.Core.Registry;

/// <summary>
/// Cache for content-addressed data (manifests and config blobs fetched by digest). Because the
/// content behind a digest can never change, entries never expire.
/// </summary>
public interface IContentCache
{
    Task<byte[]?> GetAsync(string digest, CancellationToken cancellationToken = default);

    Task SetAsync(string digest, byte[] content, CancellationToken cancellationToken = default);
}

public sealed class MemoryContentCache : IContentCache
{
    private readonly ConcurrentDictionary<string, byte[]> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<byte[]?> GetAsync(string digest, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.TryGetValue(digest, out var v) ? v : null);

    public Task SetAsync(string digest, byte[] content, CancellationToken cancellationToken = default)
    {
        _items[digest] = content;
        return Task.CompletedTask;
    }
}

/// <summary>Stores content under <c>&lt;root&gt;/sha256/&lt;hex&gt;</c>. Content is verified against its digest on read.</summary>
public sealed class DiskContentCache(string rootDirectory) : IContentCache
{
    public string RootDirectory { get; } = rootDirectory;

    public static string DefaultDirectory() => AppPaths.CacheDirectory;

    public async Task<byte[]?> GetAsync(string digest, CancellationToken cancellationToken = default)
    {
        var path = PathFor(digest);
        if (path is null || !File.Exists(path)) return null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (Digests.Matches(digest, bytes)) return bytes;
            File.Delete(path); // corrupted entry
        }
        catch (IOException)
        {
        }
        return null;
    }

    public async Task SetAsync(string digest, byte[] content, CancellationToken cancellationToken = default)
    {
        var path = PathFor(digest);
        if (path is null || !Digests.Matches(digest, content)) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, content, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
            // The cache is an optimization only.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Clear()
    {
        if (Directory.Exists(RootDirectory)) Directory.Delete(RootDirectory, recursive: true);
    }

    private string? PathFor(string digest)
    {
        var parts = digest.Split(':', 2);
        if (parts.Length != 2 || parts[0] != "sha256" || parts[1].Length != 64 || !parts[1].All(Uri.IsHexDigit)) return null;
        return Path.Combine(RootDirectory, "sha256", parts[1].ToLowerInvariant());
    }
}

public static class Digests
{
    public static string Sha256(byte[] content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

    public static bool Matches(string digest, byte[] content) =>
        !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Sha256(content), digest, StringComparison.OrdinalIgnoreCase);

    public static bool IsDigest(string reference) => reference.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase);

    public static string Short(string digest) => digest.Length > 19 ? digest[..19] + "…" : digest;

    internal static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
