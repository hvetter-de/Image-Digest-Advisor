using ImageDigestAdvisor.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ImageDigestAdvisor.App.Views;

public sealed partial class AnalyzePage : Page
{
    // Keep the view model (and the last result) when navigating between pages.
    private static readonly Lazy<AnalyzeViewModel> SharedViewModel = new(() => new AnalyzeViewModel());

    public AnalyzePage()
    {
        InitializeComponent();
        SizeChanged += (_, e) => UpdateLayoutForWidth(e.NewSize.Width);
    }

    public AnalyzeViewModel ViewModel => SharedViewModel.Value;

    public static Windows.UI.Text.FontWeight WeightIf(bool emphasis) =>
        emphasis ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public static FontFamily MonoFontIf(bool monospace) =>
        monospace ? (FontFamily)Application.Current.Resources["MonoFont"] : new FontFamily("Segoe UI Variable Text, Segoe UI");

    /// <summary>Side-by-side cards on wide windows, stacked on narrow ones.</summary>
    private void UpdateLayoutForWidth(double width)
    {
        var narrow = width < 1000;
        Grid.SetColumn(SuggestedColumn, narrow ? 0 : 1);
        Grid.SetRow(SuggestedColumn, narrow ? 1 : 0);
        Grid.SetColumnSpan(RequestedColumn, narrow ? 2 : 1);
        Grid.SetColumnSpan(SuggestedColumn, narrow ? 2 : 1);
        SuggestedColumn.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }

    private void InputBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel.RecentInputs.Count > 0 && string.IsNullOrEmpty(InputBox.Text)) InputBox.IsSuggestionListOpen = true;
    }

    private void InputBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string chosen) ViewModel.Input = chosen;
        if (ViewModel.AnalyzeCommand.CanExecute(null)) ViewModel.AnalyzeCommand.Execute(null);
    }

    private void Example_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Input = (string)((HyperlinkButton)sender).Content;
        ViewModel.AnalyzeCommand.Execute(null);
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e) =>
        ((MainWindow)App.Current.MainWindow).NavigateTo("accounts");
}
