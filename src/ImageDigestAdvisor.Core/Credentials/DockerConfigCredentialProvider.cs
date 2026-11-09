using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ImageDigestAdvisor.Core.Credentials;

/// <summary>
/// Reads credentials created by <c>docker login</c> from <c>%USERPROFILE%\.docker\config.json</c>
/// (or <c>%DOCKER_CONFIG%</c>), including credential helpers such as <c>docker-credential-desktop</c>.
/// Docker itself does not have to be installed or running: a missing file or helper simply yields no credentials.
/// </summary>
public sealed class DockerConfigCredentialProvider : ICredentialProvider
{
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(10);

    private readonly string _configPath;
    private readonly Func<string, string?> _findHelper;
    private readonly Func<string, string, string?, CancellationToken, Task<string?>> _runHelper;

    public DockerConfigCredentialProvider(string? configPath = null)
        : this(configPath ?? DefaultConfigPath(), FindHelperOnDisk, RunHelperAsync)
    {
    }

    internal DockerConfigCredentialProvider(
        string configPath,
        Func<string, string?> findHelper,
        Func<string, string, string?, CancellationToken, Task<string?>> runHelper)
    {
        _configPath = configPath;
        _findHelper = findHelper;
        _runHelper = runHelper;
    }

    public static string DefaultConfigPath()
    {
        var dir = Environment.GetEnvironmentVariable("DOCKER_CONFIG");
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker");
        }
        return Path.Combine(dir, "config.json");
    }

    public async Task<RegistryCredential?> GetAsync(string registry, CancellationToken cancellationToken = default)
    {
        var config = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (config is null) return null;

        var target = RegistryNames.Normalize(registry);

        // 1. Registry-specific helper ("credHelpers": { "myacr.azurecr.io": "acr-env" })
        foreach (var (server, helper) in config.CredHelpers)
        {
            if (RegistryNames.Normalize(server) == target)
            {
                var c = await GetFromHelperAsync(helper, server, target, cancellationToken).ConfigureAwait(false);
                if (c is not null) return c;
            }
        }

        // 2. Inline credentials ("auths": { "...": { "auth": "base64(user:pass)" } })
        foreach (var (server, entry) in config.Auths)
        {
            if (RegistryNames.Normalize(server) != target) continue;
            var c = FromAuthEntry(target, entry);
            if (c is not null) return c;
        }

        // 3. Default store ("credsStore": "desktop")
        if (config.CredsStore is { } store)
        {
            foreach (var server in CandidateServerUrls(target, config.Auths.Keys))
            {
                var c = await GetFromHelperAsync(store, server, target, cancellationToken).ConfigureAwait(false);
                if (c is not null) return c;
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<RegistryAccount>> ListAsync(CancellationToken cancellationToken = default)
    {
        var config = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (config is null) return [];

        var accounts = new Dictionary<string, RegistryAccount>();

        void Add(string server, string? username)
        {
            var registry = RegistryNames.Normalize(server);
            if (registry.Length == 0) return;
            if (!accounts.TryGetValue(registry, out var existing) || string.IsNullOrEmpty(existing.Username))
            {
                accounts[registry] = new RegistryAccount(registry, username ?? string.Empty, CredentialSource.DockerConfig);
            }
        }

        foreach (var (server, entry) in config.Auths)
        {
            Add(server, FromAuthEntry(server, entry)?.Username);
        }

        foreach (var server in config.CredHelpers.Keys)
        {
            Add(server, null);
        }

        if (config.CredsStore is { } store && _findHelper(store) is { } helperPath)
        {
            var json = await _runHelper(helperPath, "list", null, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        Add(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null);
                    }
                }
                catch (JsonException)
                {
                }
            }
        }

        return accounts.Values
            .Select(a => a.Username == "<token>" ? a with { Username = "(token)" } : a)
            .OrderBy(a => a.Registry, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<RegistryCredential?> GetFromHelperAsync(string helper, string serverUrl, string registry, CancellationToken ct)
    {
        var path = _findHelper(helper);
        if (path is null) return null;

        var json = await _runHelper(path, "get", serverUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var user = root.TryGetProperty("Username", out var u) ? u.GetString() : null;
            var secret = root.TryGetProperty("Secret", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(secret)) return null;
            return new RegistryCredential(registry, user, secret, CredentialSource.DockerConfig);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RegistryCredential? FromAuthEntry(string registry, AuthEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.IdentityToken))
        {
            return new RegistryCredential(RegistryNames.Normalize(registry), "<token>", entry.IdentityToken, CredentialSource.DockerConfig);
        }

        if (!string.IsNullOrEmpty(entry.Auth))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(entry.Auth));
                var colon = decoded.IndexOf(':');
                if (colon > 0)
                {
                    return new RegistryCredential(RegistryNames.Normalize(registry), decoded[..colon], decoded[(colon + 1)..], CredentialSource.DockerConfig);
                }
            }
            catch (FormatException)
            {
            }
        }

        if (!string.IsNullOrEmpty(entry.Username) && !string.IsNullOrEmpty(entry.Password))
        {
            return new RegistryCredential(RegistryNames.Normalize(registry), entry.Username, entry.Password, CredentialSource.DockerConfig);
        }

        return null;
    }

    /// <summary>Credential helpers are keyed by the exact server string used at login, so try the usual spellings.</summary>
    private static IEnumerable<string> CandidateServerUrls(string registry, IEnumerable<string> knownServers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var known in knownServers)
        {
            if (RegistryNames.Normalize(known) == registry && seen.Add(known)) yield return known;
        }

        if (registry == "docker.io")
        {
            if (seen.Add("https://index.docker.io/v1/")) yield return "https://index.docker.io/v1/";
        }
        else
        {
            if (seen.Add(registry)) yield return registry;
            if (seen.Add("https://" + registry)) yield return "https://" + registry;
        }
    }

    private async Task<DockerConfig?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_configPath)) return null;

        try
        {
            await using var stream = File.OpenRead(_configPath);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            var config = new DockerConfig();
            if (root.TryGetProperty("auths", out var auths) && auths.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in auths.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Object) continue;
                    config.Auths[p.Name] = new AuthEntry(
                        GetString(p.Value, "auth"),
                        GetString(p.Value, "username"),
                        GetString(p.Value, "password"),
                        GetString(p.Value, "identitytoken"));
                }
            }

            if (root.TryGetProperty("credHelpers", out var helpers) && helpers.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in helpers.EnumerateObject())
                {
                    if (p.Value.GetString() is { Length: > 0 } h) config.CredHelpers[p.Name] = h;
                }
            }

            config.CredsStore = GetString(root, "credsStore") is { Length: > 0 } cs ? cs : null;
            return config;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? FindHelperOnDisk(string helper)
    {
        var exe = $"docker-credential-{helper}.exe";
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin"));

        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    private static async Task<string?> RunHelperAsync(string helperPath, string command, string? input, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HelperTimeout);

        var psi = new ProcessStartInfo(helperPath, command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;

            if (input is not null) await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            process.StandardInput.Close();

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private sealed class DockerConfig
    {
        public Dictionary<string, AuthEntry> Auths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> CredHelpers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? CredsStore { get; set; }
    }

    private sealed record AuthEntry(string? Auth, string? Username, string? Password, string? IdentityToken);
}
