namespace Coanda.Daemon.GitHub;

/// <summary>
/// The primary handler the GitHub client uses when the daemon's options supply none: it sends
/// nothing and fails every request, so a step that needs GitHub pauses rather than reaching the
/// network from a host that was not meant to. <c>coa daemon run</c> supplies the real handler.
/// </summary>
public sealed class NoGitHubHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException(
            "this daemon was started with no GitHub handler, so it sends no request to GitHub."));
}
