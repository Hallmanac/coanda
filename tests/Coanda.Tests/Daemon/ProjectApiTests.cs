using System.Net;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.Flows;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

public sealed class ProjectApiTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task The_cli_client_adds_shows_and_lists_projects_through_the_running_daemon()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);

        ProjectView added = await client.PostAsync<ProjectView>(
            "/projects", new ProjectAddRequest("coanda", "git@github.com:you/coanda.git", "main"), None);
        added.Name.Should().Be("coanda");
        await client.PostAsync<ProjectView>("/projects", new ProjectAddRequest("beta", "https://example.invalid/beta.git", "trunk"), None);

        ProjectView shown = await client.GetAsync<ProjectView>("/projects/coanda", None);
        shown.Remote.Should().Be("git@github.com:you/coanda.git");
        shown.PrimaryBranch.Should().Be("main");

        List<ProjectView> listed = await client.GetAsync<List<ProjectView>>("/projects", None);
        listed.Select(p => (p.Name, p.Remote, p.PrimaryBranch)).Should().Equal(
            ("beta", "https://example.invalid/beta.git", "trunk"),
            ("coanda", "git@github.com:you/coanda.git", "main"));

        string[] awkwardNames = ["team/coanda", "50% done", "a%2Fb"];
        foreach (string awkward in awkwardNames)
        {
            await client.PostAsync<ProjectView>("/projects", new ProjectAddRequest(awkward, "https://example.invalid/x.git", "main"), None);
            (await client.GetAsync<ProjectView>(ProjectEndpoints.PathOf(awkward), None)).Name.Should().Be(awkward, "the show path carries any name");
        }

        await FluentActions.Awaiting(() => client.PostAsync<ProjectView>(
                "/projects", new ProjectAddRequest("coanda", "https://example.invalid/again.git", "main"), None))
            .Should().ThrowAsync<DomainException>().WithMessage("Project 'coanda' is already registered*");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Showing_a_project_with_no_record_answers_404_with_a_rule_naming_coa_project_add()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);

        await FluentActions.Awaiting(() => client.GetAsync<ProjectView>("/projects/nowhere", None))
            .Should().ThrowAsync<DomainException>().WithMessage("Project 'nowhere' is not registered; register it with coa project add nowhere*");

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{daemon.Info.BaseAddress}/projects/nowhere");
        request.Headers.Add(DaemonHost.TokenHeader, daemon.Info.Token);
        using HttpResponseMessage response = await http.SendAsync(request, None);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Adding_a_parcel_through_the_endpoint_in_a_project_with_no_record_is_refused_with_the_rule()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);

        await FluentActions.Awaiting(() => client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest("nowhere", "task", "Lost", null), None))
            .Should().ThrowAsync<DomainException>().WithMessage("Project 'nowhere' is not registered; register it with coa project add nowhere*");
        (await client.GetAsync<List<ParcelView>>("/parcels?project=nowhere", None)).Should().BeEmpty();

        await client.PostAsync<ProjectView>("/projects", new ProjectAddRequest("nowhere", "https://example.invalid/nowhere.git", "main"), None);
        (await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest("nowhere", "task", "Found", null), None)).Project.Should().Be("nowhere");

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
        await Flowing.UntilAsync(
            async () => (await client.GetAsync<List<ParcelView>>("/parcels?project=alpha", None)).Count == 1,
            "the registered project's subscription created its parcel");
        (await client.GetAsync<List<ParcelView>>("/parcels?project=unregistered", None)).Should().BeEmpty();

        // The stock daemon's empty scripted executor pauses the flow at its first judgment; waiting for that keeps the stop from racing the runner.
        ParcelView alpha = (await client.GetAsync<List<ParcelView>>("/parcels?project=alpha", None)).Single();
        FlowView bound = await Flowing.BoundAsync(client, "alpha", alpha.Id);
        await Flowing.UntilAsync(client, "alpha", bound.Id, "paused");

        await daemon.StopAsync(None);
    }
}
