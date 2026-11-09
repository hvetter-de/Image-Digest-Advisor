using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageDigestAdvisor.Core.Credentials;

namespace ImageDigestAdvisor.App.ViewModels;

public sealed partial class AccountItemViewModel(RegistryAccount account, AccountsViewModel owner) : ObservableObject
{
    public string Registry => account.Registry;

    public string Username => string.IsNullOrEmpty(account.Username) ? "(stored by Docker)" : account.Username;

    public string SourceText => account.Source == CredentialSource.App ? "Saved in this app" : "Docker login (read-only)";

    public bool CanRemove => account.Source == CredentialSource.App;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [RelayCommand]
    private Task TestAsync() => owner.TestAsync(this);

    [RelayCommand]
    private void Remove() => owner.Remove(this);
}

public sealed partial class AccountsViewModel : ObservableObject
{
    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial int Count { get; set; }

    public bool IsEmpty => Count == 0;

    public async Task LoadAsync()
    {
        var accounts = await Task.Run(() => App.Current.Services.Credentials.ListAsync());
        Accounts.Clear();
        foreach (var a in accounts) Accounts.Add(new AccountItemViewModel(a, this));
        Count = Accounts.Count;
    }

    internal async Task TestAsync(AccountItemViewModel item)
    {
        item.Status = "Testing…";
        var services = App.Current.Services;
        var credential = await Task.Run(() => services.Credentials.GetAsync(item.Registry));
        if (credential is null)
        {
            item.Status = "⚠ Docker's credential helper is not available, so this login cannot be used.";
            return;
        }

        var result = await Task.Run(() => services.Registry.TestCredentialsAsync(item.Registry, credential.Username, credential.Secret));
        item.Status = (result.Success ? "✓ " : "⚠ ") + result.Message;
    }

    internal void Remove(AccountItemViewModel item)
    {
        App.Current.Services.AppCredentials.Remove(item.Registry);
        App.Current.Services.Registry.ClearTokens();
        _ = LoadAsync();
    }

    public Task<Core.Registry.CredentialTestResult> TestNewAsync(string registry, string username, string secret) =>
        Task.Run(() => App.Current.Services.Registry.TestCredentialsAsync(registry, username, secret));

    public async Task SaveAsync(string registry, string username, string secret)
    {
        App.Current.Services.AppCredentials.Save(registry, username, secret);
        App.Current.Services.Registry.ClearTokens();
        await LoadAsync();
    }
}
