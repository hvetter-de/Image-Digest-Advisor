using ImageDigestAdvisor.Core.Analysis;
using ImageDigestAdvisor.Core.References;
using ImageDigestAdvisor.Core.Registry;

namespace ImageDigestAdvisor.Core.History;

/// <summary>One image in the history of a tag.</summary>
public sealed record TagHistoryEntry
{
    /// <summary>The digest to pin (index digest for multi-arch images).</summary>
    public required string Digest { get; init; }

    /// <summary>Tags that point to this digest today; empty if none does any more.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>When the tag was pushed, if the registry reports it (Docker Hub).</summary>
    public DateTimeOffset? Pushed { get; init; }

    /// <summary>Compressed size of the chosen platform, if the tag list reports it (Docker Hub).</summary>
    public long? ListedSize { get; init; }

    /// <summary>Docker Hub tag metadata, lets the inspector skip the index download.</summary>
    public TagInfo? Hint { get; init; }

    /// <summary>The moving tag points to this digest today.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>This is the image the user checked.</summary>
    public bool IsUserImage { get; init; }

    /// <summary>When this app saw the moving tag point here (local record), if it did.</summary>
    public DigestObservation? Seen { get; init; }

    /// <summary>Already loaded details (the user's image); others are loaded on demand.</summary>
    public ImageDetails? Details { get; init; }

    /// <summary>The release tag this entry was found by, e.g. "vnext-EN20261123".</summary>
    public string? ReleaseTag { get; init; }

    /// <summary>Release tag, else the most specific tag, else the digest.</summary>
    public string Label => ReleaseTag ?? (Tags.Count > 0 ? VersionMath.PickBestTag(Tags) ?? Tags[0] : Digest);
}

public sealed record TagHistory
{
    /// <summary>The repository, without tag or digest.</summary>
    public required ImageReference Repository { get; init; }

    public required string Tag { get; init; }

    public required Platform Platform { get; init; }

    /// <summary>Where the tag points today, if it still exists.</summary>
    public string? CurrentDigest { get; init; }

    /// <summary>Images that still have a tag, newest first.</summary>
    public IReadOnlyList<TagHistoryEntry> Releases { get; init; } = [];

    /// <summary>Older images no tag points to any more: seen by this app, or the user's image.</summary>
    public IReadOnlyList<TagHistoryEntry> Untagged { get; init; } = [];

    /// <summary>How the releases were found, e.g. "dated tags like vnext-EN20261123"; null if there are none.</summary>
    public string? Source { get; init; }

    /// <summary>Number of release tags found before the <see cref="TagHistoryService.MaxReleaseTags"/> limit.</summary>
    public int ReleaseTagCount { get; init; }

    /// <summary>Loading details costs Docker Hub pulls (manifest downloads count against the limit).</summary>
    public bool DetailsCostPulls { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Lists the images a tag pointed to in the past: release tags published next to it (see <see cref="TagFamily"/>)
/// plus the digests this app saw (see <see cref="DigestHistoryStore"/>).
/// </summary>
public sealed class TagHistoryService(RegistryClient registry, ITagCatalog catalog, ImageInspector inspector, DigestHistoryStore store)
{
    /// <summary>Only the newest release tags are resolved (one HEAD request each on registries without a tag API).</summary>
    public int MaxReleaseTags { get; init; } = 60;

    public DigestHistoryStore Store => store;

    /// <param name="reference">Repository and tag (<c>latest</c> if none).</param>
    /// <param name="userImage">The image the user checked, to mark it in the list.</param>
    public async Task<TagHistory> GetAsync(ImageReference reference, Platform platform, ImageDetails? userImage = null, CancellationToken ct = default)
    {
        var repo = reference.WithTag(null).WithDigest(null);
        var tag = reference.Tag ?? "latest";
        var warnings = new List<string>();

        TagList? tags = null;
        try
        {
            tags = await catalog.GetTagsAsync(repo, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is RegistryException or HttpRequestException && !ct.IsCancellationRequested)
        {
            warnings.Add($"Could not list tags: {e.Message}");
        }

        var info = tags?.Tags.Where(t => t.Digest is not null).ToDictionary(t => t.Name, StringComparer.Ordinal);

        // Where the tag points today.
        string? current = info?.GetValueOrDefault(tag)?.Digest;
        if (current is null)
        {
            try
            {
                current = await registry.ResolveDigestAsync(repo, tag, ct).ConfigureAwait(false);
            }
            catch (RegistryException e) when (e.Kind == RegistryErrorKind.NotFound)
            {
                warnings.Add($"The tag '{tag}' doesn't exist (any more).");
            }
        }
        if (current is not null) store.Record(repo, tag, current);

        // Release tags and their digests.
        var family = TagFamily.Find(tag, tags?.Tags.Select(t => t.Name) ?? []);
        var releaseTags = family.Tags.Take(MaxReleaseTags).ToList();
        var digests = await ResolveAsync(repo, releaseTags, info, ct).ConfigureAwait(false);

        var releases = new List<TagHistoryEntry>();
        foreach (var name in releaseTags)
        {
            if (!digests.TryGetValue(name, out var digest)) continue;
            if (releases.Any(r => SameDigest(r.Digest, digest))) continue;
            releases.Add(Entry(digest, name));
        }

        TagHistoryEntry Entry(string digest, string firstTag)
        {
            var hint = info?.GetValueOrDefault(firstTag);
            // All tags of this image: from the tag API if it has digests, otherwise the release tags we resolved.
            var tagsOfImage = info is not null
                ? info.Values.Where(t => t.Contains(digest)).Select(t => t.Name).ToList()
                : digests.Where(d => SameDigest(d.Value, digest)).Select(d => d.Key).ToList();
            if (SameDigest(digest, current) && !tagsOfImage.Contains(tag)) tagsOfImage.Insert(0, tag);
            if (!tagsOfImage.Contains(firstTag)) tagsOfImage.Add(firstTag);

            return new TagHistoryEntry
            {
                Digest = digest,
                Tags = tagsOfImage,
                ReleaseTag = firstTag == tag ? null : firstTag,
                Hint = hint,
                Pushed = hint?.LastPushed,
                ListedSize = hint is null ? null : SizeFor(hint, platform),
                IsCurrent = SameDigest(digest, current),
            };
        }

        // The current image is always part of the history, even without release tags.
        if (current is not null && !releases.Any(r => r.IsCurrent))
        {
            releases.Insert(0, Entry(current, tag));
        }

        // Local record: when the app saw the tag point to each digest.
        var untagged = new List<TagHistoryEntry>();
        foreach (var seen in store.Get(repo, tag))
        {
            var index = releases.FindIndex(r => SameDigest(r.Digest, seen.Digest));
            if (index >= 0)
            {
                if (releases[index].Seen is null) releases[index] = releases[index] with { Seen = seen };
            }
            else if (!untagged.Any(u => SameDigest(u.Digest, seen.Digest)))
            {
                untagged.Add(new TagHistoryEntry { Digest = seen.Digest, Seen = seen });
            }
        }

        // The user's image.
        if (userImage is not null)
        {
            var index = releases.FindIndex(r => userImage.HasDigest(r.Digest));
            if (index >= 0)
            {
                releases[index] = releases[index] with { IsUserImage = true, Details = userImage };
            }
            else
            {
                var u = untagged.FindIndex(r => userImage.HasDigest(r.Digest));
                var entry = new TagHistoryEntry
                {
                    Digest = userImage.PinnableDigest,
                    Tags = userImage.Tags,
                    IsUserImage = true,
                    Details = userImage,
                    Seen = u >= 0 ? untagged[u].Seen : null,
                };
                if (u >= 0) untagged[u] = entry;
                else untagged.Insert(0, entry);
            }
        }

        if (family.Tags.Count > releaseTags.Count)
        {
            warnings.Add($"Showing the {releaseTags.Count} newest of {family.Tags.Count} release tags.");
        }

        return new TagHistory
        {
            Repository = repo,
            Tag = tag,
            Platform = platform,
            CurrentDigest = current,
            Releases = releases,
            Untagged = untagged,
            Source = family.Description,
            ReleaseTagCount = family.Tags.Count,
            DetailsCostPulls = repo.IsDockerHub,
            Warnings = warnings,
        };
    }

    /// <summary>Loads created date, size, labels, … of one entry. Content is cached by digest, so this is cheap the second time.</summary>
    public async Task<ImageDetails> LoadDetailsAsync(TagHistory history, TagHistoryEntry entry, CancellationToken ct = default)
    {
        if (entry.Details is not null) return entry.Details;
        var details = await inspector.InspectAsync(history.Repository, entry.Digest, history.Platform, entry.Hint, ct).ConfigureAwait(false);
        return details with { Tags = entry.Tags, Reference = details.Reference.WithTag(entry.Tags.Count > 0 ? entry.Label : null) };
    }

    /// <summary>Remembers where the checked tag points today, so the history grows even without release tags.</summary>
    public void RecordAnalysis(AnalysisResult result)
    {
        if (result.EffectiveTag is not { } tag || result.TagWasInferred) return;

        var sameTag = result.Suggestions.FirstOrDefault(s => s.Kind == SuggestionKind.SameTag || s.AlsoKinds.Contains(SuggestionKind.SameTag));
        var current = sameTag?.Details
                      ?? (result.Findings.Any(f => f.Kind == FindingKind.UpToDate) ? result.Requested : null);
        if (current is null) return;

        store.Record(result.Requested.Reference.WithTag(null).WithDigest(null), tag, current.PinnableDigest);
    }

    private async Task<Dictionary<string, string>> ResolveAsync(ImageReference repo, IReadOnlyList<string> names, Dictionary<string, TagInfo>? info, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var name in names)
        {
            if (info?.GetValueOrDefault(name)?.Digest is { } d) result[name] = d;
            else missing.Add(name);
        }

        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(missing.Select(async name =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var digest = await registry.ResolveDigestAsync(repo, name, ct).ConfigureAwait(false);
                lock (result) result[name] = digest;
            }
            catch (RegistryException)
            {
                // Tag deleted meanwhile or not readable: leave it out.
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return result;
    }

    private static long? SizeFor(TagInfo hint, Platform platform)
    {
        if (hint.PlatformDigests.Count == 0) return hint.Sizes.Count == 1 ? hint.Sizes.Values.First() : null;
        var chosen = ImageInspector.Select(hint.PlatformDigests.Select(p => p.Platform).ToList(), platform);
        if (chosen is null) return null;
        var digest = hint.PlatformDigests.First(p => p.Platform == chosen).Digest;
        return hint.Sizes.TryGetValue(digest, out var size) ? size : null;
    }

    private static bool SameDigest(string? a, string? b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
