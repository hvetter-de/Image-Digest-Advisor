using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageDigestAdvisor.Core.Analysis;
using ImageDigestAdvisor.Core.References;
using ImageDigestAdvisor.Core.Registry;

namespace ImageDigestAdvisor.App.ViewModels;

public sealed partial class AnalyzeViewModel : ObservableObject
{
    private static readonly string[] KnownPlatforms =
        ["linux/amd64", "linux/arm64", "linux/arm/v7", "linux/arm/v6", "linux/386", "linux/ppc64le", "linux/s390x", "linux/riscv64", "windows/amd64"];

    public AnalyzeViewModel()
    {
        var settings = App.Current.Settings;
        PlatformText = KnownPlatforms.Contains(settings.Platform) ? settings.Platform : KnownPlatforms[0];
        MaxAgeDays = settings.MaxAgeDays;
        foreach (var r in settings.RecentInputs) RecentInputs.Add(r);
    }

    public IReadOnlyList<string> Platforms => KnownPlatforms;

    public ObservableCollection<string> RecentInputs { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    public partial string Input { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlatformText { get; set; }

    [ObservableProperty]
    public partial double MaxAgeDays { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    [ObservableProperty]
    public partial bool ErrorSuggestsAccount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(ShowWelcome))]
    public partial ResultViewModel? Result { get; set; }

    public bool HasResult => Result is not null;

    public bool ShowWelcome => Result is null && !IsBusy && ErrorMessage is null;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowWelcome));

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(ShowWelcome));

    private bool CanAnalyze() => !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanAnalyze), IncludeCancelCommand = true)]
    private async Task AnalyzeAsync(CancellationToken ct)
    {
        ErrorMessage = null;
        ErrorSuggestsAccount = false;

        if (!ImageReference.TryParse(Input, out _, out var parseError))
        {
            ErrorMessage = parseError;
            return;
        }

        Platform platform;
        try
        {
            platform = Platform.Parse(string.IsNullOrWhiteSpace(PlatformText) ? "linux/amd64" : PlatformText);
        }
        catch (FormatException e)
        {
            ErrorMessage = e.Message;
            return;
        }

        IsBusy = true;
        Result = null;
        try
        {
            var maxAge = double.IsNaN(MaxAgeDays) || MaxAgeDays <= 0 ? (int?)null : (int)MaxAgeDays;
            var result = await Task.Run(() => App.Current.Services.Analyzer.AnalyzeAsync(Input, new AnalysisOptions { Platform = platform, MaxAgeDays = maxAge }, ct), ct);
            Result = new ResultViewModel(result);

            var settings = App.Current.Settings;
            settings.Platform = platform.ToString();
            settings.MaxAgeDays = maxAge ?? 0;
            settings.AddRecent(Input.Trim());
            settings.Save();
            RecentInputs.Clear();
            foreach (var r in settings.RecentInputs) RecentInputs.Add(r);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Cancelled.";
        }
        catch (RegistryException e)
        {
            ErrorMessage = e.Message;
            ErrorSuggestsAccount = e.Kind is RegistryErrorKind.Unauthorized or RegistryErrorKind.RateLimited;
        }
        catch (Exception e)
        {
            ErrorMessage = $"Unexpected error: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
