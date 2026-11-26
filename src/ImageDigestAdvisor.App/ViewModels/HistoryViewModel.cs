using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageDigestAdvisor.Core.Analysis;
using ImageDigestAdvisor.Core.History;
using ImageDigestAdvisor.Core.References;
using ImageDigestAdvisor.Core.Registry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ImageDigestAdvisor.App.ViewModels;

/// <summary>What the History page shows: a repository tag, the platform and (optionally) the user's image.</summary>
public sealed record HistoryRequest(ImageReference Reference, Platform Platform, ImageDetails? UserImage);

/// <summary>The History page: images a tag pointed to in the past.</summary>
public sealed partial class HistoryViewModel(HistoryRequest request) : ObservableObject
{
    /// <summary>Height of the tallest bar in the size chart.</summary>
    public const double ChartHeight = 64;

    private CancellationTokenSource _cts = new();

    public HistoryRequest Request { get; } = request;

    public string Tag => Request.Reference.Tag ?? "latest";

    public string Title => $"History of '{Tag}'";

    public string RepositoryText => Request.Reference.WithTag(null).WithDigest(null).ToString();

    public ObservableCollection<HistoryEntryViewModel> Releases { get; } = [];

    /// <summary>Releases oldest first, for the size chart.</summary>
    public ObservableCollection<HistoryEntryViewModel> ChartEntries { get; } = [];

    public ObservableCollection<HistoryEntryViewModel> Untagged { get; } = [];

    public TagHistory? History { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoaded))]
    public partial bool IsLoading { get; set; } = true;

    public bool IsLoaded => !IsLoading && ErrorMessage is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsLoaded))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    [ObservableProperty]
    public partial string SourceText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial string Notes { get; set; } = string.Empty;

    public bool HasNotes => Notes.Length > 0;

    [ObservableProperty]
    public partial bool HasNoReleaseTags { get; set; }

    [ObservableProperty]
    public partial string NoReleaseTagsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasUntagged { get; set; }

    [ObservableProperty]
    public partial bool HasChart { get; set; }

    [ObservableProperty]
    public partial bool HasUserImageInChart { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompare))]
    public partial int SelectedCount { get; set; }

    public bool CanCompare => SelectedCount == 2;

    public string CompareHint => SelectedCount switch
    {
        0 => "Tick two images to compare them.",
        1 => "Tick one more image to compare.",
        2 => "Ready to compare.",
        _ => "Tick exactly two images to compare.",
    };

    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(CompareHint));

    public IReadOnlyList<HistoryEntryViewModel> SelectedEntries => Releases.Concat(Untagged).Where(e => e.IsSelected).ToList();

    public void Cancel() => _cts.Cancel();

    public async Task LoadAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsLoading = true;
        ErrorMessage = null;

        var service = App.Current.Services.History;
        TagHistory history;
        try
        {
            history = await Task.Run(() => service.GetAsync(Request.Reference, Request.Platform, Request.UserImage, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e) when (e is RegistryException or HttpRequestException)
        {
            ErrorMessage = e.Message;
            IsLoading = false;
            return;
        }

        History = history;
        Releases.Clear();
        Untagged.Clear();
        ChartEntries.Clear();
        foreach (var entry in history.Releases) Releases.Add(new HistoryEntryViewModel(this, entry));
        foreach (var entry in history.Untagged) Untagged.Add(new HistoryEntryViewModel(this, entry));
        foreach (var entry in Releases.Reverse()) ChartEntries.Add(entry);
        HasUntagged = Untagged.Count > 0;
        HasUserImageInChart = Releases.Any(r => r.IsUserImage);

        var recorded = history.Releases.Concat(history.Untagged).Count(e => e.Seen is not null);
        var parts = new List<string>();
        parts.Add(history.Source is not null
            ? $"{history.Releases.Count(r => r.ReleaseTag is not null)} releases found via {history.Source}"
            : "No release tags found");
        parts.Add(history.Platform.ToString());
        if (recorded > 0) parts.Add($"{recorded} {(recorded == 1 ? "digest" : "digests")} recorded by this app");
        SourceText = string.Join("  ·  ", parts);

        var notes = new List<string>(history.Warnings);
        if (history.DetailsCostPulls)
        {
            notes.Add("Docker Hub: dates and sizes come from the Docker Hub tag list. Opening an image downloads its manifest, which counts as a pull.");
        }
        Notes = string.Join("\n", notes);

        HasNoReleaseTags = history.Source is null;
        var firstSeen = history.Releases.Concat(history.Untagged).Select(e => e.Seen?.FirstSeen).Where(d => d is not null).DefaultIfEmpty().Min();
        NoReleaseTagsText = $"The registry publishes no release tags next to '{Tag}', and registries keep no history of where a tag pointed. "
                            + "This app records the digest each time you check this tag"
                            + (firstSeen is { } f ? $"; recorded since {f.ToLocalTime():yyyy-MM-dd}." : ".");

        IsLoading = false;
        RefreshDerived();

        // Details (created date, size, …) are free on most registries; on Docker Hub each costs a pull, so load on demand there.
        if (!history.DetailsCostPulls)
        {
            await LoadAllDetailsAsync(ct);
        }
    }

    private async Task LoadAllDetailsAsync(CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(4);
        var tasks = Releases.Concat(Untagged).Select(async e =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await e.LoadDetailsAsync(ct);
            }
            finally
            {
                gate.Release();
            }
        });
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task<ImageDetails?> LoadDetailsAsync(TagHistoryEntry entry, CancellationToken ct)
    {
        if (History is not { } history) return null;
        var service = App.Current.Services.History;
        return await Task.Run(() => service.LoadDetailsAsync(history, entry, ct), ct);
    }

    public CancellationToken Token => _cts.Token;

    /// <summary>Recomputes size changes and chart bars after details arrive.</summary>
    internal void RefreshDerived()
    {
        HistoryEntryViewModel? older = null;
        foreach (var entry in Releases.Reverse())
        {
            entry.SetOlder(older);
            if (entry.Size is not null) older = entry;
        }

        var sizes = Releases.Select(r => r.Size).OfType<long>().ToList();
        HasChart = sizes.Count >= 2;
        if (sizes.Count == 0) return;
        var max = sizes.Max();
        foreach (var entry in Releases)
        {
            // Proportional to the size (bars start at zero), so small changes don't look dramatic.
            entry.BarHeight = entry.Size is { } s && max > 0 ? Math.Max(2, ChartHeight * s / max) : 2;
        }
    }

    internal void UpdateSelection() => SelectedCount = Releases.Concat(Untagged).Count(e => e.IsSelected);

    /// <summary>The entry released before <paramref name="entry"/>, for "changes vs. previous".</summary>
    internal HistoryEntryViewModel? PreviousOf(HistoryEntryViewModel entry)
    {
        var index = Releases.IndexOf(entry);
        return index >= 0 && index + 1 < Releases.Count ? Releases[index + 1] : null;
    }

    internal static Brush ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var b) && b is Brush brush ? brush : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}

/// <summary>One row of the history list.</summary>
public sealed partial class HistoryEntryViewModel : ObservableObject
{
    private readonly HistoryViewModel _owner;
    private Task? _loading;

    public HistoryEntryViewModel(HistoryViewModel owner, TagHistoryEntry entry)
    {
        _owner = owner;
        Entry = entry;
        Details = entry.Details;
    }

    public TagHistoryEntry Entry { get; }

    public string Title => Entry.Tags.Count > 0 ? Entry.Label : "Untagged digest";

    public string OtherTags
    {
        get
        {
            var others = Entry.Tags.Where(t => t != Entry.Label).ToList();
            return others.Count switch
            {
                0 => string.Empty,
                > 6 => "also " + string.Join(", ", others.Take(6)) + $" … (+{others.Count - 6})",
                _ => "also " + string.Join(", ", others),
            };
        }
    }

    public bool HasOtherTags => OtherTags.Length > 0;

    public string ShortDigest => Entry.Digest.StartsWith("sha256:", StringComparison.Ordinal) && Entry.Digest.Length > 19
        ? Entry.Digest[..19] + "…"
        : Entry.Digest;

    public bool IsCurrent => Entry.IsCurrent;

    public bool IsUserImage => Entry.IsUserImage;

    public string? SeenText => Entry.Seen is { } s
        ? s.FirstSeen.Date == s.LastSeen.Date
            ? $"'{_owner.Tag}' pointed here when checked on {s.FirstSeen.ToLocalTime():yyyy-MM-dd}"
            : $"'{_owner.Tag}' pointed here when checked between {s.FirstSeen.ToLocalTime():yyyy-MM-dd} and {s.LastSeen.ToLocalTime():yyyy-MM-dd}"
        : null;

    public bool HasSeenText => SeenText is not null;

    /// <summary>The pinned reference users can paste: repo:tag@digest, or repo@digest for untagged images.</summary>
    public string PinnedReference
    {
        get
        {
            var repo = _owner.Request.Reference.WithTag(null);
            return (Entry.Tags.Count > 0 ? repo.WithTag(Entry.Label) : repo).WithDigest(Entry.Digest).ToString();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DateText), nameof(Size), nameof(SizeText), nameof(LayersText), nameof(HasCard))]
    public partial ImageDetails? Details { get; set; }

    [ObservableProperty]
    public partial ImageCardViewModel? Card { get; set; }

    public bool HasCard => Card is not null;

    partial void OnCardChanged(ImageCardViewModel? value) => OnPropertyChanged(nameof(HasCard));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadButton))]
    public partial bool IsLoadingDetails { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    public partial string? LoadError { get; set; }

    public bool HasLoadError => LoadError is not null;

    /// <summary>Docker Hub: details are loaded on demand.</summary>
    public bool ShowLoadButton => Details is null && !IsLoadingDetails;

    partial void OnDetailsChanged(ImageDetails? value) => OnPropertyChanged(nameof(ShowLoadButton));

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && Card is null) _ = ExpandAsync();
    }

    /// <summary>Loads this image (and, where free, the previous release for "changes vs.") and builds the details card.</summary>
    private async Task ExpandAsync()
    {
        var ct = _owner.Token;
        await LoadDetailsAsync(ct);
        if (_owner.PreviousOf(this) is { Details: null } previous && _owner.History is { DetailsCostPulls: false })
        {
            await previous.LoadDetailsAsync(ct);
        }
        BuildCard();
    }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => _owner.UpdateSelection();

    public DateTimeOffset? Date => Details?.Created ?? Entry.Pushed ?? Entry.Seen?.FirstSeen;

    public string DateText => Date is { } d
        ? $"{d.ToLocalTime():yyyy-MM-dd}"
        : IsLoadingDetails ? "…" : "–";

    public string DateTooltip => Details?.Created is not null ? "Created (from the image config)"
        : Entry.Pushed is not null ? "Pushed to Docker Hub"
        : Entry.Seen is not null ? "First seen by this app"
        : "Not loaded yet";

    public long? Size => Details?.CompressedSize ?? Entry.ListedSize;

    public string SizeText => Size is { } s ? ImageCardViewModel.FormatSize(s) : "–";

    public string LayersText => Details is { } d ? $"{d.LayerCount} layers" : string.Empty;

    [ObservableProperty]
    public partial string DeltaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Brush? DeltaBrush { get; set; }

    [ObservableProperty]
    public partial double BarHeight { get; set; } = 2;

    public string BarTooltip => $"{Title}\n{DateText} · {SizeText}";

    public Brush BarBrush => HistoryViewModel.ThemeBrush(IsUserImage ? "SystemFillColorCautionBrush"
        : IsCurrent ? "AccentFillColorDefaultBrush"
        : "ControlStrongFillColorDefaultBrush");

    internal void SetOlder(HistoryEntryViewModel? older)
    {
        if (Size is not { } size || older?.Size is not { } previous)
        {
            DeltaText = string.Empty;
            return;
        }
        var delta = size - previous;
        var abs = ImageCardViewModel.FormatSize(Math.Abs(delta));
        (DeltaText, DeltaBrush) = delta switch
        {
            0 => ("±0", HistoryViewModel.ThemeBrush("TextFillColorTertiaryBrush")),
            > 0 => ($"+{abs}", HistoryViewModel.ThemeBrush("SystemFillColorCautionBrush")),
            _ => ($"−{abs}", HistoryViewModel.ThemeBrush("SystemFillColorSuccessBrush")),
        };
        OnPropertyChanged(nameof(BarTooltip));
    }

    public Task LoadDetailsAsync(CancellationToken ct) => _loading ??= LoadCoreAsync(ct);

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        try
        {
            if (Details is null)
            {
                IsLoadingDetails = true;
                OnPropertyChanged(nameof(DateText));
                Details = await _owner.LoadDetailsAsync(Entry, ct);
                _owner.RefreshDerived();
            }
        }
        catch (OperationCanceledException)
        {
            _loading = null;
        }
        catch (Exception e) when (e is RegistryException or HttpRequestException)
        {
            LoadError = e.Message;
            _loading = null;
        }
        finally
        {
            IsLoadingDetails = false;
            OnPropertyChanged(nameof(DateText));
            OnPropertyChanged(nameof(DateTooltip));
            OnPropertyChanged(nameof(BarTooltip));
        }
    }

    /// <summary>Card with all details, compared with the previous release if that one is loaded.</summary>
    internal void BuildCard()
    {
        if (Details is not { } details) return;
        var previous = _owner.PreviousOf(this);
        Card = new ImageCardViewModel(Title, OtherTags, PinnedReference, details, DateTimeOffset.UtcNow,
            compareTo: previous?.Details, comparedWith: previous?.Title);
        Card.OfferSecurityCheck();
    }
}
