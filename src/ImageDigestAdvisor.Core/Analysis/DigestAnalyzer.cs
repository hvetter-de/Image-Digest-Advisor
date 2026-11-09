using ImageDigestAdvisor.Core.References;
using ImageDigestAdvisor.Core.Registry;

namespace ImageDigestAdvisor.Core.Analysis;

/// <summary>
/// Answers "is this image digest outdated, and what should I use instead?".
/// </summary>
public sealed class DigestAnalyzer(RegistryClient registry, ITagCatalog catalog, ImageInspector inspector, TimeProvider? timeProvider = null)
{
    /// <summary>Maximum number of tags probed with HEAD requests on registries without a digest-aware tag API.</summary>
    public int MaxTagProbes { get; init; } = 150;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<AnalysisResult> AnalyzeAsync(string input, AnalysisOptions options, CancellationToken ct = default)
    {
        var reference = ImageReference.Parse(input);
        var repo = reference.WithTag(null).WithDigest(null);
        var warnings = new List<string>();
        var findings = new List<Finding>();
        var now = _time.GetUtcNow();

        // 1. The image the user asked about.
        var requested = await inspector.InspectAsync(repo, reference.Digest ?? reference.Tag!, options.Platform, null, ct).ConfigureAwait(false);
        var platform = requested.Platform;

        if (reference.Digest is not null && requested.AvailablePlatforms.Count > 1 && !requested.Platform.Matches(options.Platform)
            && InspectorSelectFailed(requested, options.Platform))
        {
            warnings.Add($"The image is not available for {options.Platform}; showing {requested.Platform} instead.");
        }

        // 2. Tags (best effort – the analysis still works for the digest alone).
        TagList? tags = null;
        try
        {
            tags = await catalog.GetTagsAsync(repo, ct).ConfigureAwait(false);
        }
        catch (RegistryException e)
        {
            warnings.Add($"Could not list tags: {e.Message}");
        }
        catch (HttpRequestException e)
        {
            warnings.Add($"Could not list tags: {e.Message}");
        }

        // 3. Which tags point at the requested image?
        var matchingTags = await FindTagsForAsync(repo, reference, requested, tags, warnings, ct).ConfigureAwait(false);
        requested = requested with { Tags = matchingTags };

        var effectiveTag = reference.Tag;
        var inferred = false;
        if (effectiveTag is null)
        {
            effectiveTag = VersionMath.PickBestTag(matchingTags);
            if (effectiveTag is null)
            {
                var (hintTag, exact) = await ResolveVersionHintAsync(repo, requested, tags, ct).ConfigureAwait(false);
                if (hintTag is not null)
                {
                    effectiveTag = hintTag;
                    if (exact)
                    {
                        matchingTags = [.. matchingTags, hintTag];
                        requested = requested with { Tags = matchingTags };
                    }
                    else
                    {
                        inferred = true;
                    }
                }
            }
        }

        var userGaveDigest = reference.Digest is not null;
        if (userGaveDigest && reference.Tag is null && matchingTags.Count == 0 && tags is { HasDigests: true })
        {
            findings.Add(tags.Complete
                ? new Finding(FindingKind.DigestNoLongerTagged, Severity.Warning,
                    "No tag points to this digest anymore. It has most likely been replaced by a newer build.")
                : new Finding(FindingKind.DigestNoLongerTagged, Severity.Info,
                    $"This digest was not found among the {tags.Tags.Count} most recently updated tags. It is probably an older build."));
        }

        if (inferred)
        {
            warnings.Add($"The tag '{effectiveTag}' was guessed from the version the image reports about itself. Add the tag to your input for exact results.");
        }

        // 4. Candidates: where the same tag points now + newer versions in the same family.
        var candidates = new List<(SuggestionKind Kind, string Tag)>();
        if (effectiveTag is not null)
        {
            candidates.Add((SuggestionKind.SameTag, effectiveTag));
            if (tags is not null)
            {
                candidates.AddRange(VersionMath.FindNewer(effectiveTag, tags.Tags.Select(t => t.Name)));
            }
        }
        else
        {
            findings.Add(new Finding(FindingKind.TagUnknown, Severity.Info,
                "The tag of this image could not be determined, so no newer version can be suggested. Enter the image as repo:tag@digest."));
        }

        var resolved = await Task.WhenAll(candidates.Select(c => ResolveCandidateAsync(repo, c.Kind, c.Tag, platform, tags, warnings, ct))).ConfigureAwait(false);
        var candidateDetails = resolved.OfType<(SuggestionKind Kind, string Tag, ImageDetails Details)>().ToList();

        // 5. Findings.
        var sameTag = candidateDetails.FirstOrDefault(c => c.Kind == SuggestionKind.SameTag);
        if (userGaveDigest && effectiveTag is not null && sameTag.Details is not null)
        {
            if (IsSameImage(sameTag.Details, requested))
            {
                findings.Add(new Finding(FindingKind.UpToDate, Severity.Ok, $"This is still the current image for tag '{effectiveTag}'."));
            }
            else
            {
                findings.Add(new Finding(FindingKind.TagMoved, Severity.Warning, TagMovedMessage(effectiveTag, requested, sameTag.Details)));
                if (SoftwareChangesMessage(requested, sameTag.Details) is { } changes)
                {
                    findings.Add(new Finding(FindingKind.SoftwareChanges, Severity.Info, changes));
                }
            }
        }
        else if (!userGaveDigest && effectiveTag is not null)
        {
            findings.Add(new Finding(FindingKind.UpToDate, Severity.Ok, $"Showing the current image for tag '{effectiveTag}'. Pin it with the digest below."));
        }

        foreach (var c in candidateDetails)
        {
            // A tag with preview/RC software (e.g. sdk:11.0 before GA) is mentioned, but never makes the image "outdated".
            var pre = c.Details.ContainsPrereleaseSoftware && !requested.ContainsPrereleaseSoftware;
            var preNote = pre ? $" It contains pre-release software ({PrereleaseList(c.Details)}), not a stable release yet." : string.Empty;
            switch (c.Kind)
            {
                case SuggestionKind.NewestPatch:
                    findings.Add(new Finding(FindingKind.NewerPatchAvailable, pre ? Severity.Info : Severity.Warning, $"A newer patch version is available: {c.Tag}.{preNote}"));
                    break;
                case SuggestionKind.NewestMinor:
                    findings.Add(new Finding(FindingKind.NewerMinorAvailable, pre ? Severity.Info : Severity.Warning, $"A newer minor version is available: {c.Tag}.{preNote}"));
                    break;
                case SuggestionKind.NewestMajor:
                    findings.Add(new Finding(FindingKind.NewerMajorAvailable, Severity.Info, $"A newer major version exists: {c.Tag} (may contain breaking changes).{preNote}"));
                    break;
            }
        }

        if (options.MaxAgeDays is { } maxAge && requested.Age(now) is { } age && age.TotalDays > maxAge)
        {
            findings.Add(new Finding(FindingKind.OlderThanThreshold, Severity.Warning,
                $"The image was built {(int)age.TotalDays} days ago (threshold: {maxAge} days)."));
        }

        // 6. Suggestions: one per distinct image, excluding the image the user already has.
        var suggestions = candidateDetails
            .Where(c => !IsSameImage(c.Details, requested))
            .GroupBy(c => c.Details.PinnableDigest, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ordered = g.OrderBy(c => c.Kind).ToList();
                var first = ordered[0];
                var tagsOfImage = tags is { HasDigests: true }
                    ? tags.Tags.Where(t => t.Contains(first.Details.PinnableDigest) || t.Contains(first.Details.ManifestDigest)).Select(t => t.Name).ToList()
                    : ordered.Select(o => o.Tag).Distinct().ToList();
                return new Suggestion
                {
                    Kind = first.Kind,
                    Tag = first.Tag,
                    Details = first.Details with { Tags = tagsOfImage },
                    AlsoKinds = ordered.Skip(1).Select(o => o.Kind).Distinct().ToList(),
                };
            })
            .OrderBy(s => s.Kind)
            .ToList();

        var status = findings.Any(f => f.Severity == Severity.Warning)
            ? OverallStatus.Outdated
            : effectiveTag is not null ? OverallStatus.UpToDate : OverallStatus.Unknown;

        return new AnalysisResult
        {
            Input = reference,
            Requested = requested,
            EffectiveTag = effectiveTag,
            TagWasInferred = inferred,
            Status = status,
            Findings = findings,
            Suggestions = suggestions,
            Warnings = warnings,
            RateLimit = reference.IsDockerHub ? registry.DockerHubRateLimit : null,
            AnalyzedAt = now,
        };
    }

    private static bool InspectorSelectFailed(ImageDetails requested, Platform wanted) =>
        ImageInspector.Select(requested.AvailablePlatforms, wanted) is null;

    private static bool IsSameImage(ImageDetails a, ImageDetails b) =>
        a.HasDigest(b.ManifestDigest) || (b.IndexDigest is not null && a.HasDigest(b.IndexDigest));

    private static string PrereleaseList(ImageDetails details) =>
        string.Join(", ", details.SoftwareVersions.Where(v => v.IsPrerelease).Select(v => $"{v.DisplayName} {v.Value}").Take(2));

    /// <summary>Explains what the newer build of the same tag contains, based on the declared software versions.</summary>
    internal static string? SoftwareChangesMessage(ImageDetails requested, ImageDetails current)
    {
        if (requested.SoftwareVersions.Count == 0 && current.SoftwareVersions.Count == 0) return null;

        var changes = current.SoftwareChangesSince(requested);
        return changes.Count > 0
            ? "The newer build updates: " + string.Join(", ", changes) + "."
            : "The software versions are unchanged (" + string.Join(", ", current.SoftwareVersions.Select(v => $"{v.DisplayName} {v.Value}")) +
              "). The rebuild most likely contains base image or operating system security updates.";
    }

    private static string TagMovedMessage(string tag, ImageDetails requested, ImageDetails current)
    {
        var message = $"Tag '{tag}' now points to a different image";
        if (current.Created is { } c && requested.Created is { } r)
        {
            var days = (int)Math.Round((c - r).TotalDays);
            message += days > 0 ? $", built {days} days later than yours ({c:yyyy-MM-dd})" : $" (built {c:yyyy-MM-dd})";
        }
        return message + ".";
    }

    private async Task<(SuggestionKind Kind, string Tag, ImageDetails Details)?> ResolveCandidateAsync(
        ImageReference repo, SuggestionKind kind, string tag, Platform platform, TagList? tags, List<string> warnings, CancellationToken ct)
    {
        try
        {
            // The same tag is resolved via the registry (authoritative); version candidates may use Docker Hub metadata.
            var hint = kind == SuggestionKind.SameTag ? null : tags?.Tags.FirstOrDefault(t => t.Name == tag);
            var details = await inspector.InspectAsync(repo, tag, platform, hint, ct).ConfigureAwait(false);

            if (!string.Equals(details.Platform.Os, platform.Os, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(details.Platform.Architecture, platform.Architecture, StringComparison.OrdinalIgnoreCase))
            {
                lock (warnings) warnings.Add($"'{tag}' is not available for {platform}.");
                return null;
            }

            return (kind, tag, details with { Reference = details.Reference.WithTag(tag) });
        }
        catch (RegistryException e) when (e.Kind == RegistryErrorKind.NotFound)
        {
            lock (warnings) warnings.Add(kind == SuggestionKind.SameTag ? $"Tag '{tag}' no longer exists." : $"Tag '{tag}' could not be loaded.");
            return null;
        }
        catch (RegistryException e)
        {
            lock (warnings) warnings.Add($"Could not load '{tag}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Uses the version an image reports about itself to find its tag. Each candidate costs one HEAD
    /// request (free on Docker Hub). Returns whether the tag still points at exactly this image.
    /// </summary>
    private async Task<(string? Tag, bool Exact)> ResolveVersionHintAsync(ImageReference repo, ImageDetails requested, TagList? tags, CancellationToken ct)
    {
        var candidates = VersionMath.HintCandidates(requested.VersionHints, tags?.Tags.Select(t => t.Name).ToList(), tags?.Complete ?? false);
        foreach (var candidate in candidates)
        {
            try
            {
                var digest = await registry.ResolveDigestAsync(repo, candidate, ct).ConfigureAwait(false);
                return (candidate, requested.HasDigest(digest));
            }
            catch (RegistryException e) when (e.Kind == RegistryErrorKind.NotFound)
            {
            }
        }
        return (null, false);
    }

    private async Task<IReadOnlyList<string>> FindTagsForAsync(
        ImageReference repo, ImageReference input, ImageDetails requested, TagList? tags, List<string> warnings, CancellationToken ct)
    {
        if (tags is null) return input.Tag is null ? [] : [input.Tag];

        if (tags.HasDigests)
        {
            return tags.Tags
                .Where(t => t.Contains(requested.ManifestDigest) || (requested.IndexDigest is not null && t.Contains(requested.IndexDigest)))
                .Select(t => t.Name)
                .ToList();
        }

        // Registry without digest information: probing tags costs one HEAD request each, so only do it when needed.
        if (input.Tag is not null) return [input.Tag];

        var names = tags.Tags.Select(t => t.Name).ToList();
        var hintSet = requested.VersionHints.ToHashSet(StringComparer.Ordinal);
        var ordered = names
            .OrderByDescending(n => hintSet.Contains(n) || hintSet.Contains(n.TrimStart('v')))
            .ThenByDescending(n => TagVersion.TryParse(n) is not null)
            .ThenByDescending(n => TagVersion.TryParse(n), Comparer<TagVersion?>.Create((a, b) => a is null ? (b is null ? 0 : -1) : a.CompareTo(b)))
            .Take(MaxTagProbes)
            .ToList();

        if (names.Count > MaxTagProbes)
        {
            warnings.Add($"Only {MaxTagProbes} of {names.Count} tags were checked to find the tag of this digest.");
        }

        var target = requested.IndexDigest ?? requested.ManifestDigest;
        var found = new List<string>();
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(ordered.Select(async name =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var digest = await registry.ResolveDigestAsync(repo, name, ct).ConfigureAwait(false);
                if (string.Equals(digest, target, StringComparison.OrdinalIgnoreCase))
                {
                    lock (found) found.Add(name);
                }
            }
            catch (RegistryException)
            {
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return found;
    }
}
