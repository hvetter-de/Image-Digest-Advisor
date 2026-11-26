using ImageDigestAdvisor.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace ImageDigestAdvisor.App.Views;

/// <summary>Earlier images of a tag: release tags published next to it plus digests the app saw.</summary>
public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
    }

    public HistoryViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not HistoryRequest request) return;
        ViewModel = new HistoryViewModel(request);
        Bindings.Update();
        _ = ViewModel.LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel?.Cancel();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(AnalyzePage));
    }

    private static HistoryEntryViewModel? EntryOf(object sender) => (sender as FrameworkElement)?.DataContext as HistoryEntryViewModel;

    private void Bar_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        entry.IsExpanded = true;
        if (ReleasesList.ContainerFromItem(entry) is FrameworkElement container)
        {
            container.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.1, AnimationDesired = true });
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        var package = new DataPackage();
        package.SetText(entry.PinnedReference);
        Clipboard.SetContent(package);
    }

    /// <summary>Runs the normal check on this digest (e.g. to pin or roll back to it).</summary>
    private void CheckDigest_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        var check = AnalyzePage.Shared;
        check.Input = entry.PinnedReference;
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(AnalyzePage));
        if (check.AnalyzeCommand.CanExecute(null)) check.AnalyzeCommand.Execute(null);
    }

    private async void SecurityChip_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { Card: { } card, Details: { } details } entry) return;

        if (card.SecurityState == SecurityChipState.Offer)
        {
            if (!await VulnerabilityConsent.EnsureEnabledAsync(XamlRoot)) return;
            card.BeginSecurityScan();
            var scanner = App.Current.Services.Vulnerabilities;
            try
            {
                var report = await Task.Run(() => scanner.ScanAsync(details, ViewModel.Token), ViewModel.Token);
                card.SetSecurityReport(report, null);
            }
            catch (OperationCanceledException)
            {
                card.CancelSecurityScan();
            }
            return;
        }

        var dialog = new VulnerabilityDialog(card, [], $"Known vulnerabilities: {entry.Title}") { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.SelectedEntries;
        if (selected.Count != 2) return;

        await Task.WhenAll(selected.Select(s => s.LoadDetailsAsync(ViewModel.Token)));
        if (selected.Any(s => s.Details is null))
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Can't compare",
                Content = "The details of one of the images could not be loaded.",
                CloseButtonText = "Close",
            }.ShowAsync();
            return;
        }

        var ordered = selected.OrderBy(s => s.Details!.Created ?? s.Date ?? DateTimeOffset.MinValue).ToList();
        await new CompareDialog(ordered[0], ordered[1]) { XamlRoot = XamlRoot }.ShowAsync();
    }
}
