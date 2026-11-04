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

    /// <summary>How many entries a listing asks GitHub for on each page, the most it gives.</summary>
    private const int PageSize = 100;

    /// <summary>The pull request numbered <paramref name="number"/> in <paramref name="repository"/>, as a merge inspects it.</summary>
    public async Task<GitHubPullRequestState> PullRequestAsync(GitHubRepository repository, int number, string token, CancellationToken cancellationToken)
    {
        string path = $"{PathOf(repository)}/pulls/{number}";
        PullRequestStateDocument document = await GetAsync<PullRequestStateDocument>(path, token, cancellationToken);
        return new GitHubPullRequestState(
            document.Number,
            document.State ?? string.Empty,
            document.Merged ?? false,
            document.MergeCommitSha,
            document.Mergeable,
            document.Base?.Ref ?? string.Empty,
            document.Head?.Sha ?? string.Empty);
    }

    /// <summary>How many commits <paramref name="head"/> is behind <paramref name="baseBranch"/> by, as GitHub's comparison of the two reports.</summary>
    public async Task<int> BehindByAsync(GitHubRepository repository, string baseBranch, string head, string token, CancellationToken cancellationToken)
    {
        // The commits are paged and not read; only the counts are.
        string path = $"{PathOf(repository)}/compare/{EscapeRef(baseBranch)}...{EscapeRef(head)}?per_page=1";
        CompareDocument document = await GetAsync<CompareDocument>(path, token, cancellationToken);
        return document.BehindBy ?? throw new HttpRequestException($"GitHub answered GET {path} with no behind_by count.");
    }

    /// <summary>Every check run GitHub reports on the commit <paramref name="sha"/>, every page read.</summary>
    public async Task<IReadOnlyList<GitHubCheckRun>> CheckRunsAsync(GitHubRepository repository, string sha, string token, CancellationToken cancellationToken)
    {
        List<GitHubCheckRun> runs = [];
        for (int page = 1; ; page++)
        {
            string path = $"{PathOf(repository)}/commits/{EscapeRef(sha)}/check-runs?per_page={PageSize}&page={page}";
            CheckRunsDocument document = await GetAsync<CheckRunsDocument>(path, token, cancellationToken);
            IReadOnlyList<CheckRunDocument> listed = document.CheckRuns ?? [];
            runs.AddRange(listed.Select(r => new GitHubCheckRun(r.Id, r.Name ?? string.Empty, r.Status ?? string.Empty, r.Conclusion)));
            if (listed.Count < PageSize || runs.Count >= document.TotalCount)
            {
                return runs;
            }
        }
    }

    /// <summary>Every commit status posted on the commit <paramref name="sha"/>, every page read.</summary>
    public async Task<IReadOnlyList<GitHubCommitStatus>> StatusesAsync(GitHubRepository repository, string sha, string token, CancellationToken cancellationToken)
    {
        List<GitHubCommitStatus> statuses = [];
        for (int page = 1; ; page++)
        {
            string path = $"{PathOf(repository)}/commits/{EscapeRef(sha)}/statuses?per_page={PageSize}&page={page}";
            List<StatusDocument> listed = await GetAsync<List<StatusDocument>>(path, token, cancellationToken);
            statuses.AddRange(listed.Select(s => new GitHubCommitStatus(s.Id, s.Context ?? string.Empty, s.State ?? string.Empty)));
            if (listed.Count < PageSize)
            {
                return statuses;
            }
        }
    }

    /// <summary>The merge methods <paramref name="repository"/> allows; a method GitHub does not report is not allowed.</summary>
    public async Task<GitHubMergeMethods> MergeMethodsAsync(GitHubRepository repository, string token, CancellationToken cancellationToken)
    {
        RepositoryDocument document = await GetAsync<RepositoryDocument>(PathOf(repository), token, cancellationToken);
        return new GitHubMergeMethods(document.AllowMergeCommit ?? false, document.AllowSquashMerge ?? false, document.AllowRebaseMerge ?? false);
    }

    /// <summary>
    /// Merges the pull request numbered <paramref name="number"/> by <paramref name="method"/>, only while
    /// its head is <paramref name="expectedHead"/>, and returns the commit GitHub reports for the merge.
    /// </summary>
    public async Task<string> MergeAsync(
        GitHubRepository repository, int number, string expectedHead, string method, string token, CancellationToken cancellationToken)
    {
        string path = $"{PathOf(repository)}/pulls/{number}/merge";
        using HttpRequestMessage request = Request(HttpMethod.Put, path, token);
        request.Content = JsonContent.Create(new MergeRequest(expectedHead, method));
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await ThrowIfRefusedAsync(request.Method, path, response, cancellationToken);
        MergeDocument merged = await response.Content.ReadFromJsonAsync<MergeDocument>(cancellationToken)
            ?? throw new HttpRequestException($"GitHub answered PUT {path} with nothing.");
        return merged is { Merged: true, Sha: { Length: > 0 } sha }
            ? sha
            : throw new HttpRequestException($"GitHub answered PUT {path} without reporting a merge: {merged.Message ?? "it gave no message."}");
    }

    private async Task<T> GetAsync<T>(string path, string token, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Request(HttpMethod.Get, path, token);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await ThrowIfRefusedAsync(request.Method, path, response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new HttpRequestException($"GitHub answered GET {path} with nothing.");
    }

    /// <summary>A branch name or commit id as path text: each segment escaped, the slashes between them kept.</summary>
    private static string EscapeRef(string reference) => string.Join('/', reference.Split('/').Select(Uri.EscapeDataString));

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

    private sealed record MergeRequest(
        [property: JsonPropertyName("sha")] string Sha,
        [property: JsonPropertyName("merge_method")] string MergeMethod);

    private sealed record BranchDocument(
        [property: JsonPropertyName("ref")] string? Ref,
        [property: JsonPropertyName("sha")] string? Sha);

    private sealed record PullRequestStateDocument(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("merged")] bool? Merged,
        [property: JsonPropertyName("merge_commit_sha")] string? MergeCommitSha,
        [property: JsonPropertyName("mergeable")] bool? Mergeable,
        [property: JsonPropertyName("base")] BranchDocument? Base,
        [property: JsonPropertyName("head")] BranchDocument? Head);

    private sealed record CompareDocument([property: JsonPropertyName("behind_by")] int? BehindBy);

    private sealed record CheckRunDocument(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("conclusion")] string? Conclusion);

    private sealed record CheckRunsDocument(
        [property: JsonPropertyName("total_count")] int TotalCount,
        [property: JsonPropertyName("check_runs")] List<CheckRunDocument>? CheckRuns);

    private sealed record StatusDocument(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("context")] string? Context,
        [property: JsonPropertyName("state")] string? State);

    private sealed record RepositoryDocument(
        [property: JsonPropertyName("allow_merge_commit")] bool? AllowMergeCommit,
        [property: JsonPropertyName("allow_squash_merge")] bool? AllowSquashMerge,
        [property: JsonPropertyName("allow_rebase_merge")] bool? AllowRebaseMerge);

    private sealed record MergeDocument(
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("merged")] bool? Merged,
        [property: JsonPropertyName("message")] string? Message);

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
