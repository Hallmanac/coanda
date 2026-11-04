using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Store;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.Flows;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Triggers;

public sealed class SubscriptionTests
{
    private const string Project = "alpha";
    private const string Other = "beta";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal Teammate = new("teammate", Role.Member);

    private static ScriptedExecutor CleanReview() => new(new Dictionary<string, IReadOnlyList<ScriptEntry>>
    {
        ["reviewer-adversarial"] = [Scripts.Answer("clean", 1)],
    });

    private static Task RecordAsync(RunningDaemon daemon, string project, string assignee, string title) =>
        daemon.Services.GetRequiredService<TriggerService>().RecordEventAsync(
            Principal.LocalOwner, project, "pr-assigned", new Dictionary<string, string> { ["assignee"] = assignee, ["title"] = title }, None);

    private static async Task<List<ParcelView>> ParcelsAsync(DaemonClient client, string project) =>
        await client.GetAsync<List<ParcelView>>($"/parcels?project={project}", None);

    private static async Task<ParcelView> ParcelTitledAsync(DaemonClient client, string title)
    {
        ParcelView? found = null;
        await Flowing.UntilAsync(
            async () =>
            {
                found = (await ParcelsAsync(client, Project)).FirstOrDefault(p => p.Title == title);
                return found is not null;
            },
            $"a parcel titled '{title}' exists");
        return found ?? throw new InvalidOperationException("Unreachable: the wait returned without a parcel.");
    }

    [Fact]
    public async Task A_matching_external_event_creates_the_subscriptions_parcel_and_a_flow_of_its_contour_runs_once_per_event_across_a_restart()
    {
        using var temp = new TemporaryHome();
        ParcelView first;
        await using (RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = CleanReview() }, None))
        {
            using var client = new DaemonClient(temp.Home);
            await daemon.Services.GetRequiredService<TriggerService>().SubscribeAsync(
                Teammate, Project, new Trigger("pr-assigned", new Dictionary<string, string> { ["assignee"] = "brian" }),
                "task", "title", "review-on-assignment", None);

            // Recorded before the match, so the loop, which reads in order, has handled both by the time the match has its parcel.
            await RecordAsync(daemon, Project, "someone-else", "Not mine");
            await RecordAsync(daemon, Other, "brian", "In another project");
            await RecordAsync(daemon, Project, "brian", "Review my PR");

            first = await ParcelTitledAsync(client, "Review my PR");
            first.Type.Should().Be("task");
            first.State.Should().Be("refine");
            first.CreatedBy.Should().Be(Teammate.Id, "the subscription acts as the principal that recorded it");

            FlowView bound = await Flowing.BoundAsync(client, Project, first.Id);
            bound.ContourName.Should().Be("review-on-assignment");
            bound.StartedBy.Should().Be(Teammate.Id);
            (await Flowing.UntilAsync(client, Project, bound.Id, "ended")).Outcome.Should().Be("report-produced");

            (await ParcelsAsync(client, Project)).Should().ContainSingle("an event whose assignee differs creates nothing");
            (await ParcelsAsync(client, Other)).Should().BeEmpty("a subscription sees its own project's events only");
            await daemon.StopAsync(None);
        }

        await using RunningDaemon restarted = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = CleanReview() }, None);
        using var again = new DaemonClient(temp.Home);
        await RecordAsync(restarted, Project, "brian", "Review another");
        ParcelView second = await ParcelTitledAsync(again, "Review another");
        FlowView secondFlow = await Flowing.BoundAsync(again, Project, second.Id);
        (await Flowing.UntilAsync(again, Project, secondFlow.Id, "ended")).Outcome.Should().Be("report-produced");

        (await ParcelsAsync(again, Project)).Select(p => p.Title).Should().BeEquivalentTo("Review my PR", "Review another");
        (await Flowing.OfParcelAsync(again, Project, first.Id)).Should().ContainSingle();

        await restarted.StopAsync(None);
    }

    [Fact]
    public async Task A_matching_event_without_the_title_field_creates_nothing_and_the_loop_goes_on()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = CleanReview() }, None);
        using var client = new DaemonClient(temp.Home);
        TriggerService triggers = daemon.Services.GetRequiredService<TriggerService>();
        await triggers.SubscribeAsync(
            Teammate, Project, new Trigger("pr-assigned", new Dictionary<string, string> { ["assignee"] = "brian" }),
            "task", "title", "review-on-assignment", None);

        await triggers.RecordEventAsync(Principal.LocalOwner, Project, "pr-assigned", new Dictionary<string, string> { ["assignee"] = "brian" }, None);
        await RecordAsync(daemon, Project, "brian", "Titled this time");

        await ParcelTitledAsync(client, "Titled this time");
        (await ParcelsAsync(client, Project)).Should().ContainSingle();

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Recording_an_event_or_a_subscription_is_open_to_people_and_refused_to_an_agent()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var triggers = new TriggerService(store, TimeProvider.System);
        var agent = new Principal("build-session", Role.Agent);
        var fields = new Dictionary<string, string> { ["assignee"] = "brian" };

        await FluentActions.Awaiting(() => triggers.RecordEventAsync(agent, Project, "pr-assigned", fields, None))
            .Should().ThrowAsync<DomainException>().WithMessage("'event.record' is not open to the Agent role.");
        await FluentActions.Awaiting(() => triggers.SubscribeAsync(agent, Project, new Trigger("pr-assigned", fields), "task", "title", "review-on-assignment", None))
            .Should().ThrowAsync<DomainException>().WithMessage("'subscription.add' is not open to the Agent role.");

        ExternalEvent recorded = await triggers.RecordEventAsync(Teammate, Project, "pr-assigned", fields, None);
        recorded.RecordedBy.Should().Be(Teammate.Id);
        Subscription subscription = await triggers.SubscribeAsync(Principal.LocalOwner, Project, new Trigger("pr-assigned", fields), "task", "title", "review-on-assignment", None);
        (await triggers.ListSubscriptionsAsync(agent, Project, None)).Should().ContainSingle(s => s.Id == subscription.Id);
        (await triggers.ListSubscriptionsAsync(agent, Other, None)).Should().BeEmpty();

        await FluentActions.Awaiting(() => triggers.RecordEventAsync(Teammate, Tenants.Node, "pr-assigned", fields, None))
            .Should().ThrowAsync<DomainException>().WithMessage("*reserved for node-level records*");
    }
}
