using System.Net;
using ImageDigestAdvisor.Core.Analysis;
using ImageDigestAdvisor.Core.Credentials;
using ImageDigestAdvisor.Core.Registry;

namespace ImageDigestAdvisor.Core;

/// <summary>Wires up the Core services. One instance per application is enough.</summary>
public sealed class ImageDigestAdvisorServices : IDisposable
{
    private readonly bool _ownsHttp;

    public ImageDigestAdvisorServices(IAppCredentialStore appCredentials, IContentCache cache, HttpClient? http = null, string? dockerConfigPath = null)
    {
        _ownsHttp = http is null;
        Http = http ?? CreateHttpClient();
        AppCredentials = appCredentials;
        DockerCredentials = new DockerConfigCredentialProvider(dockerConfigPath);
        Credentials = new CredentialChain([AppCredentials, DockerCredentials]);
        Registry = new RegistryClient(Http, Credentials, cache);
        Tags = new TagCatalog(Http, Registry);
        Inspector = new ImageInspector(Registry);
        Analyzer = new DigestAnalyzer(Registry, Tags, Inspector);
    }

    public HttpClient Http { get; }

    public IAppCredentialStore AppCredentials { get; }

    public DockerConfigCredentialProvider DockerCredentials { get; }

    /// <summary>App accounts first, then Docker logins, then anonymous.</summary>
    public CredentialChain Credentials { get; }

    public RegistryClient Registry { get; }

    public TagCatalog Tags { get; }

    public ImageInspector Inspector { get; }

    public DigestAnalyzer Analyzer { get; }

    /// <summary>HttpClient using the Windows system proxy, with automatic decompression and redirects (blob downloads).</summary>
    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

    public void Dispose()
    {
        if (_ownsHttp) Http.Dispose();
    }
}
