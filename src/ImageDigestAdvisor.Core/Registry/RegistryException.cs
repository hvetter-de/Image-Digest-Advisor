using System.Net;

namespace ImageDigestAdvisor.Core.Registry;

public enum RegistryErrorKind
{
    NotFound,
    Unauthorized,
    RateLimited,
    Network,
    InvalidResponse,
    Unsupported,
}

public sealed record CredentialTestResult(bool Success, string Message)
{
    public static CredentialTestResult Ok { get; } = new(true, "Login works.");
}

public sealed class RegistryException(RegistryErrorKind kind, string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public RegistryErrorKind Kind { get; } = kind;

    public HttpStatusCode? StatusCode { get; } = statusCode;
}
