using ImageDigestAdvisor.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ImageDigestAdvisor.App.Views;

public sealed partial class AccountsPage : Page
{
    public AccountsPage()
    {
        InitializeComponent();
    }

    public AccountsViewModel ViewModel { get; } = new();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddAccountDialog(ViewModel) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }
}
