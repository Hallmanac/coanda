using System.Net;
using System.Text.Json;
using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.FlowRuns;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

public sealed class ProjectApiTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The request <c>coa project add</c> sends for the project <paramref name="name"/> with its repo at <paramref name="remote"/> on main.</summary>
    private static ProjectAddRequest Add(string name, string remote) =>
        new ProjectAddSettings { Name = name, Repo = remote, Branch = "main" }.ToRequest();

    [Fact]
    public async Task The_cli_client_adds_shows_and_lists_projects_through_the_running_daemon()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);

        ProjectView added = await client.PostAsync<ProjectView>(
            "/projects", Add("coanda", "git@github.com:you/coanda.git"), None);
        Assert.Equal("coanda", added.Name);
        await client.PostAsync<ProjectView>("/projects", Add("beta", "https://example.invalid/beta.git"), None);

        ProjectView shown = await client.GetAsync<ProjectView>("/projects/coanda", None);
        Assert.Equal([new ProjectRepoView("coanda", "git@github.com:you/coanda.git")], shown.Repos);

        List<ProjectView> listed = await client.GetAsync<List<ProjectView>>("/projects", None);
        Assert.Equal(
            [("beta", "beta", "https://example.invalid/beta.git"), ("coanda", "coanda", "git@github.com:you/coanda.git")],
            listed.Select(p => (p.Name, Assert.Single(p.Repos).Name, p.Repos[0].Remote)));

        string[] awkwardNames = ["team/coanda", "50% done", "a%2Fb"];
        foreach (string awkward in awkwardNames)
        {
            await client.PostAsync<ProjectView>("/projects", Add(awkward, "https://example.invalid/x.git"), None);
            // the show path carries any name
            Assert.Equal(awkward, (await client.GetAsync<ProjectView>(ProjectEndpoints.PathOf(awkward), None)).Name);
        }

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<ProjectView>(
                "/projects", Add("coanda", "https://example.invalid/again.git"), None));
        Assert.StartsWith("Project 'coanda' is already registered", exception.Message, StringComparison.Ordinal);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_project_shown_over_the_api_carries_its_repos_and_none_of_the_software_modules_settings()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await client.PostAsync<ProjectView>(
            "/projects", new ProjectAddSettings { Name = "coanda", Repo = "git@github.com:you/coanda.git", Branch = "main", Build = "make", Test = "make test" }.ToRequest(), None);

        using var http = new HttpClient();
        foreach (string path in new[] { "/projects/coanda", "/projects" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{daemon.Info.BaseAddress}{path}");
            request.Headers.Add(DaemonHost.TokenHeader, daemon.Info.Token);
            using HttpResponseMessage response = await http.SendAsync(request, None);
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(None));
            JsonElement project = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement[0] : document.RootElement;

            Assert.Equal(["name", "repos", "registeredBy", "registeredAt", "profile"], project.EnumerateObject().Select(p => p.Name));
            Assert.Equal("""[{"name":"coanda","remote":"git@github.com:you/coanda.git"}]""", project.GetProperty("repos").GetRawText());
            Assert.DoesNotContain("main", project.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("make", project.GetRawText(), StringComparison.Ordinal);
        }

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Showing_a_project_with_no_record_answers_404_with_a_rule_naming_coa_project_add()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.GetAsync<ProjectView>("/projects/nowhere", None));
        Assert.StartsWith("Project 'nowhere' is not registered; register it with coa project add nowhere", exception.Message, StringComparison.Ordinal);

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{daemon.Info.BaseAddress}/projects/nowhere");
        request.Headers.Add(DaemonHost.TokenHeader, daemon.Info.Token);
        using HttpResponseMessage response = await http.SendAsync(request, None);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Adding_a_parcel_through_the_endpoint_in_a_project_with_no_record_is_refused_with_the_rule()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest("nowhere", "task", "Lost", null), None));
        Assert.StartsWith("Project 'nowhere' is not registered; register it with coa project add nowhere", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await client.GetAsync<List<ParcelView>>("/parcels?project=nowhere", None));

        await client.PostAsync<ProjectView>("/projects", Add("nowhere", "https://example.invalid/nowhere.git"), None);
        Assert.Equal("nowhere", (await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest("nowhere", "task", "Found", null), None)).Project);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_subscription_firing_in_a_project_with_no_record_creates_no_parcel_and_the_loop_goes_on()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        TriggerService triggers = daemon.Services.GetRequiredService<TriggerService>();
        var trigger = new Trigger("pr-assigned", new Dictionary<string, string> { ["assignee"] = "brian" });
        var fields = new Dictionary<string, string> { ["assignee"] = "brian", ["title"] = "Review my PR" };
        await RegisteredProjects.AddAsync(daemon, "alpha", None);

        // Neither recording the subscription nor recording the event checks for the record.
        await triggers.SubscribeAsync(Principal.LocalOwner, "unregistered", trigger, "task", "title", "review-on-assignment", None);
        await triggers.SubscribeAsync(Principal.LocalOwner, "alpha", trigger, "task", "title", "review-on-assignment", None);
        await triggers.RecordEventAsync(Principal.LocalOwner, "unregistered", "pr-assigned", fields, None);
        await triggers.RecordEventAsync(Principal.LocalOwner, "alpha", "pr-assigned", fields, None);

        // The loop handles events in order, so once alpha's parcel exists the earlier event has been handled.
        await FlowRunDriver.UntilAsync(
            async () => (await client.GetAsync<List<ParcelView>>("/parcels?project=alpha", None)).Count == 1,
            "the registered project's subscription created its parcel");
        Assert.Empty(await client.GetAsync<List<ParcelView>>("/parcels?project=unregistered", None));

        // The stock daemon's empty scripted executor pauses the flow run at its first agent step; waiting for that keeps the stop from racing the runner.
        ParcelView alpha = (await client.GetAsync<List<ParcelView>>("/parcels?project=alpha", None)).Single();
        FlowRunView bound = await FlowRunDriver.BoundAsync(client, "alpha", alpha.Id);
        await FlowRunDriver.UntilAsync(client, "alpha", bound.Id, "paused");

        await daemon.StopAsync(None);
    }
}
