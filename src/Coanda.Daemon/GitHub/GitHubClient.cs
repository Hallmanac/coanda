using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Coanda.Daemon.GitHub;

/// <summary>
/// The daemon's one client of GitHub's REST API, a typed client of <see cref="IHttpClientFactory"/>.
/// Every request is sent to <see cref="BaseAddress"/> with a path built only from a
/// <see cref="GitHubRepository"/>, which parsed from a project's remote, and carries the token it
/// is given as its Authorization header, which the client's logging redacts. A status other than
/// success throws <see cref="GitHubRefusedException"/> quoting the status code and GitHub's message;
/// a request that reaches no answer throws <see cref="HttpRequestException"/>.
/// </summary>
public sealed class GitHubClient
{
    /// <summary>The host whose repositories the client acts on, and whose token <c>gh</c> is asked for.</summary>
    public const string Host = "github.com";

    /// <summary>The version of GitHub's REST API the requests are written against.</summary>
    public const string ApiVersion = "2022-11-28";

    /// <summary>How much of an answer that is no JSON message a refusal quotes.</summary>
    private const int QuotedCharacters = 500;

    public static readonly Uri BaseAddress = new("https://api.github.com/");

    private readonly HttpClient http;

    public GitHubClient(HttpClient http)
    {
        http.BaseAddress = BaseAddress;
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("coanda", Api.DaemonHost.Version));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", ApiVersion);
        this.http = http;
    }

    /// <summary>
    /// Registers the client with <paramref name="primaryHandler"/> as the factory of its primary
    /// message handler, which makes a new handler each time the factory rotates one, so no rotation
    /// disposes a handler something else still holds. The Authorization header is redacted from
    /// the client's logging.
    /// </summary>
    public static IHttpClientBuilder Register(IServiceCollection services, Func<HttpMessageHandler> primaryHandler) =>
        services.AddHttpClient<GitHubClient>()
            .ConfigurePrimaryHttpMessageHandler(primaryHandler)
            .RedactLoggedHeaders([HeaderNames.Authorization]);

    /// <summary>The primary handler that reaches the network, which <c>coa daemon run</c> supplies.</summary>
    public static HttpMessageHandler NetworkHandler() => new SocketsHttpHandler();

    /// <summary>The open pull request whose head is <paramref name="branch"/> in <paramref name="repository"/>, or null when it has none.</summary>
    public async Task<GitHubPullRequest?> FindOpenAsync(GitHubRepository repository, string branch, string token, CancellationToken cancellationToken)
    {
        string head = Uri.EscapeDataString($"{repository.Owner}:{branch}");
        string path = $"{PathOf(repository)}/pulls?state=open&head={head}";
        using HttpRequestMessage request = Request(HttpMethod.Get, path, token);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await ThrowIfRefusedAsync(request.Method, path, response, cancellationToken);
        List<PullRequestDocument> listed = await response.Content.ReadFromJsonAsync<List<PullRequestDocument>>(cancellationToken) ?? [];
        return listed
            .Where(p => string.Equals(p.Head?.Ref, branch, StringComparison.Ordinal))
            .Select(p => p.ToPullRequest())
            .FirstOrDefault();
    }

    /// <summary>Opens a pull request from <paramref name="branch"/> against <paramref name="baseBranch"/> in <paramref name="repository"/>.</summary>
    public async Task<GitHubPullRequest> OpenAsync(
        GitHubRepository repository, string branch, string baseBranch, string title, string body, string token, CancellationToken cancellationToken)
    {
        string path = $"{PathOf(repository)}/pulls";
        using HttpRequestMessage request = Request(HttpMethod.Post, path, token);
        request.Content = JsonContent.Create(new OpenRequest(title, branch, baseBranch, body));
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await ThrowIfRefusedAsync(request.Method, path, response, cancellationToken);
        PullRequestDocument opened = await response.Content.ReadFromJsonAsync<PullRequestDocument>(cancellationToken)
            ?? throw new HttpRequestException($"GitHub answered POST {path} with no pull request.");
        return opened.ToPullRequest();
    }

    private static string PathOf(GitHubRepository repository) =>
        $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}";

    private static HttpRequestMessage Request(HttpMethod method, string path, string token) =>
        new(method, path) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };

    private static async Task ThrowIfRefusedAsync(HttpMethod method, string path, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new GitHubRefusedException(method, path, response.StatusCode, MessageOf(body));
    }

    /// <summary>GitHub's own <c>message</c> from an answer's body, or the start of the body when it carries none.</summary>
    private static string MessageOf(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Not JSON; the body itself is quoted below.
        }

        string trimmed = body.Trim();
        return trimmed.Length switch
        {
            0 => "it gave no message.",
            > QuotedCharacters => trimmed[..QuotedCharacters],
            _ => trimmed,
        };
    }

    private sealed record OpenRequest(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("head")] string Head,
        [property: JsonPropertyName("base")] string Base,
        [property: JsonPropertyName("body")] string Body);

    private sealed record HeadDocument([property: JsonPropertyName("ref")] string? Ref);

    private sealed record PullRequestDocument(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("head")] HeadDocument? Head)
    {
        public GitHubPullRequest ToPullRequest() =>
            HtmlUrl is { Length: > 0 } url
                ? new GitHubPullRequest(Number, url)
                : throw new HttpRequestException($"GitHub answered with pull request {Number} but no web URL for it.");
    }
}
