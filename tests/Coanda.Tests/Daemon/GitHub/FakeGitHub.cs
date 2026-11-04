using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Daemon.GitHub;

namespace Coanda.Tests.Daemon.GitHub;

/// <summary>
/// GitHub as the daemon's client sees it, in memory, so no test reaches the network. It records
/// every request. A test can script the answer to any request; otherwise the lookup of open pull
/// requests for a head answers with the ones this fake opened for that head, which is none until it
/// opens one, and the request that opens one answers <see cref="Number"/> and <see cref="Url"/>.
/// Any other request is no request a test scripted: it is recorded in <see cref="Unexpected"/> and
/// fails, with a message naming it, so the step that sent it pauses saying so.
/// </summary>
public sealed class FakeGitHub
{
    /// <summary>The number of the pull request the fake opens.</summary>
    public const int Number = 17;

    /// <summary>The web URL of the pull request the fake opens.</summary>
    public const string Url = "https://github.com/example/repo/pull/17";

    private readonly Lock gate = new();
    private readonly List<Sent> requests = [];
    private readonly List<string> unexpected = [];
    private readonly List<(Func<Sent, bool> When, Func<Sent, HttpResponseMessage> Answer)> scripted = [];
    private readonly List<(string Head, int Number, string Url)> opened = [];

    /// <summary>One request as it was sent: its method, its URL, its Authorization header, and its body.</summary>
    public sealed record Sent(HttpMethod Method, Uri Uri, string? Authorization, string? Body)
    {
        public string PathAndQuery => Uri.PathAndQuery;

        public bool IsLookup => Method == HttpMethod.Get && Uri.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal);

        public bool IsOpen => Method == HttpMethod.Post && Uri.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal);
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

            string described = $"{sent.Method} {sent.PathAndQuery}";
            unexpected.Add(described);
            throw new InvalidOperationException($"The fake GitHub was sent {described}, which no test scripted.");
        }
    }

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
