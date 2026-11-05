using System.Net;

namespace Coanda.Modules.Software.GitHub;

/// <summary>GitHub answered a request with a status other than success; the message quotes the status code and GitHub's own message.</summary>
public sealed class GitHubRefusedException(HttpMethod method, string path, HttpStatusCode status, string message)
    : Exception($"GitHub answered {method} {path} with status {(int)status} ({status}): {message}")
{
    /// <summary>The status GitHub answered with.</summary>
    public HttpStatusCode Status { get; } = status;
}
