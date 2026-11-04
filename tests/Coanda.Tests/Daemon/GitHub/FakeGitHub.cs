using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Daemon.GitHub;
using Coanda.Tests.Daemon.Commands;

namespace Coanda.Tests.Daemon.GitHub;

/// <summary>
/// GitHub as the daemon's client sees it, in memory, so no test reaches the network. It records
/// every request. A test can script the answer to any request; otherwise the lookup of open pull
/// requests for a head answers with the ones this fake opened for that head, which is none until it
/// opens one, and the request that opens one answers <see cref="Number"/> and <see cref="Url"/>.
/// A merge's requests are answered as a clean merge would want them by default: any pull request
/// it is asked for is open on <see cref="RegisteredProjects.Branch"/> with the head
/// <see cref="ScriptedCommandRunner.PrimaryHead"/>, the commit the default open-pr path records,
/// mergeable, and behind its base by no commits; that head carries one check run, named
/// <see cref="RegisteredProjects.RequiredCheck"/>, which succeeded, and no status; the repository
/// allows every merge method; and the merge request is accepted, answering <see cref="MergeCommit"/>,
/// after which the pull request reads as merged with that commit. Any other request is no request a test scripted: it is recorded in <see cref="Unexpected"/> and
/// fails, with a message naming it, so the step that sent it pauses saying so.
/// </summary>
public sealed class FakeGitHub
{
    /// <summary>The number of the pull request the fake opens.</summary>
    public const int Number = 17;

    /// <summary>The web URL of the pull request the fake opens.</summary>
    public const string Url = "https://github.com/example/repo/pull/17";

    /// <summary>The commit the fake reports for a merge it accepted.</summary>
    public const string MergeCommit = "fedcba9876543210fedcba9876543210fedcba98";

    private readonly Lock gate = new();
    private readonly List<Sent> requests = [];
    private readonly List<string> unexpected = [];
    private readonly List<(Func<Sent, bool> When, Func<Sent, HttpResponseMessage> Answer)> scripted = [];
    private readonly List<(string Head, int Number, string Url)> opened = [];
    private readonly HashSet<string> merged = new(StringComparer.Ordinal);

    /// <summary>One request as it was sent: its method, its URL, its Authorization header, and its body.</summary>
    public sealed record Sent(HttpMethod Method, Uri Uri, string? Authorization, string? Body)
    {
        public string PathAndQuery => Uri.PathAndQuery;

        public bool IsLookup => Method == HttpMethod.Get && Uri.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal);

        public bool IsOpen => Method == HttpMethod.Post && Uri.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal);

        /// <summary>A read of one pull request: <c>GET repos/{owner}/{repo}/pulls/{number}</c>.</summary>
        public bool IsPullRequestRead => Method == HttpMethod.Get && Segments is [_, _, _, "pulls", _];

        /// <summary>A merge request: <c>PUT repos/{owner}/{repo}/pulls/{number}/merge</c>.</summary>
        public bool IsMerge => Method == HttpMethod.Put && Segments is [_, _, _, "pulls", _, "merge"];

        /// <summary>A comparison of two commits: <c>GET repos/{owner}/{repo}/compare/{base}...{head}</c>.</summary>
        public bool IsCompare => Method == HttpMethod.Get && Segments is [_, _, _, "compare", ..];

        /// <summary>A page of a commit's check runs.</summary>
        public bool IsCheckRuns => Method == HttpMethod.Get && Segments is [_, _, _, "commits", .., "check-runs"];

        /// <summary>A page of a commit's statuses.</summary>
        public bool IsStatuses => Method == HttpMethod.Get && Segments is [_, _, _, "commits", .., "statuses"];

        /// <summary>A read of the repository itself, for the merge methods it allows.</summary>
        public bool IsRepositoryRead => Method == HttpMethod.Get && Segments is ["repos", _, _];

        /// <summary>The page a listing asks for, 1 when it names none.</summary>
        public int Page => int.TryParse(System.Web.HttpUtility.ParseQueryString(Uri.Query)["page"], out int page) ? page : 1;

        /// <summary>Any request a merge sends.</summary>
        public bool IsMergeTraffic => IsPullRequestRead || IsMerge || IsCompare || IsCheckRuns || IsStatuses || IsRepositoryRead;

        /// <summary>The unescaped path's segments.</summary>
        public string[] Segments => [.. Uri.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString)];
    }

    /// <summary>The factory the daemon's options take: a new handler over this fake each call, so rotating one disposes nothing shared.</summary>
    public Func<HttpMessageHandler> Handler => () => new Handling(this);

    /// <summary>A client over this fake, as the factory would build it, for a test that builds the action itself.</summary>
    public GitHubClient Client() => new(new HttpClient(new Handling(this)));

    public IReadOnlyList<Sent> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    public IReadOnlyList<string> Unexpected
    {
        get
        {
            lock (gate)
            {
                return [.. unexpected];
            }
        }
    }

    /// <summary>Answers every request <paramref name="when"/> matches through <paramref name="answer"/>; the latest script that matches wins.</summary>
    public FakeGitHub On(Func<Sent, bool> when, Func<Sent, HttpResponseMessage> answer)
    {
        lock (gate)
        {
            scripted.Add((when, answer));
        }

        return this;
    }

    /// <summary>Answers every request <paramref name="when"/> matches with <paramref name="status"/> and GitHub's error shape carrying <paramref name="message"/>.</summary>
    public FakeGitHub Refuse(Func<Sent, bool> when, HttpStatusCode status, string message) =>
        On(when, _ => Json(status, new JsonObject { ["message"] = message }));

    /// <summary>Makes the fake hold an open pull request for <paramref name="head"/>, as one opened before would.</summary>
    public void HoldOpen(string head, int number, string url)
    {
        lock (gate)
        {
            opened.Add((head, number, url));
        }
    }

    public static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private HttpResponseMessage Answer(Sent sent)
    {
        Func<Sent, HttpResponseMessage>? answer;
        lock (gate)
        {
            requests.Add(sent);
            answer = scripted.LastOrDefault(s => s.When(sent)).Answer;
        }

        if (answer is not null)
        {
            return answer(sent);
        }

        lock (gate)
        {
            if (sent.IsLookup && HeadOf(sent) is { } head)
            {
                return Json(HttpStatusCode.OK, new JsonArray([.. opened.Where(o => o.Head == head).Select(o => PullRequest(o.Number, o.Url, o.Head))]));
            }

            if (sent.IsOpen && sent.Body is { } body && JsonNode.Parse(body)?["head"]?.GetValue<string>() is { } branch)
            {
                opened.Add((branch, Number, Url));
                return Json(HttpStatusCode.Created, PullRequest(Number, Url, branch));
            }

            if (MergeAnswer(sent) is { } answered)
            {
                return answered;
            }

            string described = $"{sent.Method} {sent.PathAndQuery}";
            unexpected.Add(described);
            throw new InvalidOperationException($"The fake GitHub was sent {described}, which no test scripted.");
        }
    }

    /// <summary>The default answer to a merge's request, or null when the request is none of a merge's.</summary>
    private HttpResponseMessage? MergeAnswer(Sent sent)
    {
        if (sent.IsPullRequestRead)
        {
            string number = sent.Segments[4];
            return Json(HttpStatusCode.OK, PullRequestState(int.Parse(number, System.Globalization.CultureInfo.InvariantCulture), merged.Contains(number)));
        }

        if (sent.IsMerge)
        {
            merged.Add(sent.Segments[4]);
            return Json(HttpStatusCode.OK, new JsonObject { ["sha"] = MergeCommit, ["merged"] = true, ["message"] = "Pull Request successfully merged" });
        }

        if (sent.IsCompare)
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["status"] = "ahead", ["ahead_by"] = 1, ["behind_by"] = 0 });
        }

        if (sent.IsCheckRuns)
        {
            return Json(HttpStatusCode.OK, CheckRuns(sent.Page == 1 ? [CheckRun(1, RegisteredProjects.RequiredCheck, "completed", "success")] : [], total: 1));
        }

        if (sent.IsStatuses)
        {
            return Json(HttpStatusCode.OK, new JsonArray());
        }

        return sent.IsRepositoryRead
            ? Json(HttpStatusCode.OK, MergeMethods(merge: true, squash: true, rebase: true))
            : null;
    }

    /// <summary>A pull request as GitHub reads it to a merge, open on the default branch with the default head unless said otherwise.</summary>
    public static JsonObject PullRequestState(
        int number,
        bool merged = false,
        string state = "open",
        bool? mergeable = true,
        string baseRef = RegisteredProjects.Branch,
        string head = ScriptedCommandRunner.PrimaryHead) => new()
    {
        ["number"] = number,
        ["state"] = merged ? "closed" : state,
        ["merged"] = merged,
        ["merge_commit_sha"] = merged ? MergeCommit : null,
        ["mergeable"] = mergeable,
        ["base"] = new JsonObject { ["ref"] = baseRef, ["sha"] = "0000000000000000000000000000000000000000" },
        ["head"] = new JsonObject { ["ref"] = "coanda/branch", ["sha"] = head },
    };

    /// <summary>One check run as GitHub lists it.</summary>
    public static JsonObject CheckRun(long id, string name, string status, string? conclusion) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["status"] = status,
        ["conclusion"] = conclusion,
    };

    /// <summary>A page of check runs, with the total count across every page.</summary>
    public static JsonObject CheckRuns(IReadOnlyList<JsonObject> runs, int total) => new()
    {
        ["total_count"] = total,
        ["check_runs"] = new JsonArray([.. runs]),
    };

    /// <summary>One commit status as GitHub lists it.</summary>
    public static JsonObject Status(long id, string context, string state) => new()
    {
        ["id"] = id,
        ["context"] = context,
        ["state"] = state,
    };

    /// <summary>A repository as GitHub reads it to a merge, allowing the methods given.</summary>
    public static JsonObject MergeMethods(bool merge, bool squash, bool rebase) => new()
    {
        ["allow_merge_commit"] = merge,
        ["allow_squash_merge"] = squash,
        ["allow_rebase_merge"] = rebase,
    };

    /// <summary>The branch a lookup's <c>head</c> query names, after its <c>owner:</c>.</summary>
    private static string? HeadOf(Sent sent)
    {
        string? head = System.Web.HttpUtility.ParseQueryString(sent.Uri.Query)["head"];
        int colon = head?.IndexOf(':') ?? -1;
        return colon < 0
            ? null
            : head?[(colon + 1)..];
    }

    private static JsonObject PullRequest(int number, string url, string head) => new()
    {
        ["number"] = number,
        ["html_url"] = url,
        ["head"] = new JsonObject { ["ref"] = head },
    };

    private sealed class Handling(FakeGitHub fake) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var sent = new Sent(request.Method, request.RequestUri ?? new Uri("about:blank"), request.Headers.Authorization?.ToString(), body);
            return fake.Answer(sent);
        }
    }
}
