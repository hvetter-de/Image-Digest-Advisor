using ImageDigestAdvisor.App.ViewModels;
using ImageDigestAdvisor.Core.Credentials;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ImageDigestAdvisor.App.Views;

public sealed partial class AddAccountDialog : ContentDialog
{
    private readonly AccountsViewModel _viewModel;

    public AddAccountDialog(AccountsViewModel viewModel, string? registry = null)
    {
        _viewModel = viewModel;
        InitializeComponent();
        RegistryBox.Text = registry ?? "docker.io";
        UpdateHint();
    }

    private string Registry => RegistryNames.Normalize(RegistryBox.Text ?? string.Empty);

    private void RegistryBox_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args) => UpdateHint(args.Text);

    private void RegistryBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHint(RegistryBox.SelectedItem as string);

    private void UpdateHint(string? text = null)
    {
        var registry = RegistryNames.Normalize(text ?? RegistryBox.Text ?? string.Empty);
        HintText.Text = registry switch
        {
            "docker.io" => "Docker Hub: create a personal access token with 'Public repo read-only' access at app.docker.com › Account settings › Personal access tokens. A free account raises the rate limit.",
            "ghcr.io" => "GitHub: use your GitHub username and a personal access token (classic) with the read:packages scope.",
            "quay.io" => "Quay: use a robot account name and its token.",
            _ when registry.EndsWith(".azurecr.io", StringComparison.Ordinal) => "Azure Container Registry: use a repository-scoped token (Registry › Tokens) or the admin user. Username and password come from the Azure portal.",
            _ => "Use the username and token or password you would use with 'docker login'.",
        };
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate(out var error))
        {
            ResultText.Text = error;
            return;
        }

        TestButton.IsEnabled = false;
        TestProgress.IsActive = true;
        ResultText.Text = string.Empty;
        try
        {
            var result = await _viewModel.TestNewAsync(Registry, UserBox.Text.Trim(), SecretBox.Password);
            ResultText.Text = (result.Success ? "✓ " : "⚠ ") + result.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
            TestProgress.IsActive = false;
        }
    }

    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!Validate(out var error))
        {
            ResultText.Text = error;
            args.Cancel = true;
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            await _viewModel.SaveAsync(Registry, UserBox.Text.Trim(), SecretBox.Password);
        }
        catch (Exception ex)
        {
            ResultText.Text = "⚠ " + ex.Message;
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private bool Validate(out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(Registry) || Registry.Contains(' ')) error = "Please enter a registry, e.g. docker.io.";
        else if (string.IsNullOrWhiteSpace(UserBox.Text)) error = "Please enter a username.";
        else if (string.IsNullOrEmpty(SecretBox.Password)) error = "Please enter an access token or password.";
        return error.Length == 0;
    }
}
