using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Contours;
using Coanda.Core.Flows;
using Coanda.Core.Flows.Events;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Flows;

/// <summary>
/// A judgment's session reaches the daemon with a token of its own, which resolves to an Agent-role
/// principal bound to its flow and launch: it may record on that flow alone while that launch runs,
/// and stops resolving, for good, once the launch ends.
/// </summary>
public sealed class SessionPrincipalTests
{
    private const string Project = "alpha";
    private const string Other = "beta";
    private const string Single = "one-judgment";
    private const string NoValidToken = "The call carried no valid daemon token.";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task A_launch_gets_a_token_that_resolves_to_its_flow_and_launch_and_a_session_pausing_its_own_flow_ends_it()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(BuildScript(Scripts.Answer("done", 1)));
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await PublishSingleAsync(daemon, Project);
        using var owner = new DaemonClient(temp.Home, sessionToken: null);
        FlowView flow = await StartSingleAsync(owner, Project, "Talks back");
        await executor.Collecting.WaitAsync(Patience);

        SessionAccess access = AccessOf(executor, flow.Id);
        access.NodeHome.Should().Be(temp.Home.Path);
        access.Project.Should().Be(Project);
        Guid launch = await LaunchOfAsync(daemon, Project, flow.Id);
        Principal? resolved = await daemon.Services.GetRequiredService<SessionTokens>().ResolveAsync(access.Token, None);
        resolved.Should().Be(new Principal($"session:{launch}", Role.Agent, new SessionBinding(Project, flow.Id, launch)));

        // A query is open to the session; its pause of its own running flow passes and is recorded as
        // the session's, whatever principal header the client sent.
        using var session = new DaemonClient(temp.Home, access.Token);
        (await Flowing.ShowAsync(session, Project, flow.Id)).Status.Should().Be("running");
        FlowView paused = await Flowing.PauseAsync(session, Project, flow.Id, "The session asks for a person.");
        paused.Status.Should().Be("paused");
        (await EventsAsync(daemon, Project, flow.Id)).OfType<FlowPaused>().Should().ContainSingle()
            .Which.PausedBy.Should().Be($"session:{launch}");

        (await StatusAsync(daemon, access.Token, HttpMethod.Get, $"/flows?project={Project}", null)).Should().Be(HttpStatusCode.Unauthorized);
        await FluentActions.Awaiting(() => Flowing.ShowAsync(session, Project, flow.Id))
            .Should().ThrowAsync<DomainException>().WithMessage(NoValidToken);

        // The daemon token still acts as the owner: stop is closed to the Agent role.
        await Flowing.UntilAsync(() => executor.Inner.Stops.Count == 1, "the paused judgment's session is stopped");
        await Flowing.StopAsync(owner, Project, flow.Id, "Taking it by hand.");
        (await EventsAsync(daemon, Project, flow.Id)).OfType<FlowStopped>().Should().ContainSingle()
            .Which.StoppedBy.Should().Be(Environment.UserName);

        // The token is in neither the flow's view nor any event recorded on the flow.
        using HttpClient http = Http(daemon);
        using var show = new HttpRequestMessage(HttpMethod.Get, $"/flows/{flow.Id}?project={Project}");
        show.Headers.Add(DaemonHost.TokenHeader, daemon.Info.Token);
        using HttpResponseMessage shown = await http.SendAsync(show, None);
        (await shown.Content.ReadAsStringAsync(None)).Should().NotContain(access.Token);
        foreach (object recorded in await EventsAsync(daemon, Project, flow.Id))
        {
            JsonSerializer.Serialize(recorded, recorded.GetType()).Should().NotContain(access.Token);
        }

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_session_is_refused_with_its_flows_rule_on_another_flow_another_project_and_any_start_and_nothing_is_recorded()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(BuildScript());
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await RegisteredProjects.AddAsync(daemon, Other, None);
        await PublishSingleAsync(daemon, Project);
        await PublishSingleAsync(daemon, Other);
        using var owner = new DaemonClient(temp.Home, sessionToken: null);
        FlowView own = await StartSingleAsync(owner, Project, "Its own");
        FlowView sibling = await StartSingleAsync(owner, Project, "A sibling");
        FlowView elsewhere = await StartSingleAsync(owner, Other, "Elsewhere");
        await Flowing.UntilAsync(() => executor.Inner.Spawns.Count == 3, "every flow has launched its judgment");

        SessionAccess access = AccessOf(executor, own.Id);
        using var session = new DaemonClient(temp.Home, access.Token);
        string rule = $"A session's principal records only on flow {Ids.Short(own.Id)} in project '{Project}'*";

        // A spawn returning is not the end of a launch's writes: the runner records the session after
        // it, then holds at the closed gate. Count each other flow's events only once it has settled.
        await SettledAsync(daemon, Project, sibling.Id);
        await SettledAsync(daemon, Other, elsewhere.Id);
        int siblingEvents = (await EventsAsync(daemon, Project, sibling.Id)).Count;
        int elsewhereEvents = (await EventsAsync(daemon, Other, elsewhere.Id)).Count;

        await FluentActions.Awaiting(() => Flowing.PauseAsync(session, Project, sibling.Id, "Not mine."))
            .Should().ThrowAsync<DomainException>().WithMessage(rule);
        await FluentActions.Awaiting(() => Flowing.PauseAsync(session, Other, elsewhere.Id, "Not mine either."))
            .Should().ThrowAsync<DomainException>().WithMessage(rule);
        ParcelView spare = await owner.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Never started", null), None);
        await FluentActions.Awaiting(() => Flowing.StartAsync(session, Project, spare.Id, Single))
            .Should().ThrowAsync<DomainException>().WithMessage(rule + "a flow it starts is never its own*");
        (await StatusAsync(daemon, access.Token, HttpMethod.Post, $"/flows/{sibling.Id}/pause", new FlowPauseRequest(Project, "Not mine.")))
            .Should().Be(HttpStatusCode.UnprocessableEntity);

        // Every flow recording entry refuses another flow in the service, beside the registry check.
        FlowService flows = daemon.Services.GetRequiredService<FlowService>();
        Principal principal = Principal.ForSession(new SessionBinding(Project, own.Id, await LaunchOfAsync(daemon, Project, own.Id)));
        JsonElement value = JsonDocument.Parse("""{"verdict":"done","summary":"no"}""").RootElement.Clone();
        Func<Task>[] writes =
        [
            () => flows.StartStepAsync(principal, Project, sibling.Id, "build", None),
            () => flows.RespawnAsync(principal, Project, sibling.Id, "build", None),
            () => flows.SpawnedAsync(principal, Project, sibling.Id, "build", ShippedAgents.Build.Hash, ShippedAgents.Build.Model, None),
            () => flows.SessionOpenedAsync(principal, Project, sibling.Id, "build", Ids.New(), None),
            () => flows.RecordAnswerAsync(principal, Project, sibling.Id, "build", "build", Ids.New(), ShippedAgents.Build.Hash, ShippedAgents.Build.Model, value, Scripts.Usage(1), None),
            () => flows.RecordResultAsync(principal, Project, sibling.Id, "build", "open-pr", value, None),
            () => flows.PauseAsync(principal, Project, sibling.Id, "Not mine.", FlowLevers.PerformAgain("build"), None),
            () => flows.EndAsync(principal, Project, sibling.Id, "built", None),
            () => flows.EndAsync(principal, Other, own.Id, "built", None),
            () => flows.FollowAsync(principal, Project, own.Id, Single, None),
        ];
        foreach (Func<Task> write in writes)
        {
            await write.Should().ThrowAsync<DomainException>().WithMessage(rule);
        }

        (await EventsAsync(daemon, Project, sibling.Id)).Should().HaveCount(siblingEvents, "a refused write appends nothing");
        (await EventsAsync(daemon, Other, elsewhere.Id)).Should().HaveCount(elsewhereEvents, "a refused write appends nothing");
        (await Flowing.OfParcelAsync(owner, Project, spare.Id)).Should().BeEmpty("a refused start starts nothing");

        // Queries stay open to the session in any project, and the refusals leave its token resolving.
        (await session.GetAsync<List<FlowView>>($"/flows?project={Other}", None)).Should().ContainSingle();
        (await StatusAsync(daemon, access.Token, HttpMethod.Get, $"/flows?project={Project}", null)).Should().Be(HttpStatusCode.OK);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_token_still_resolves_during_the_retry_after_an_invalid_answer_and_stops_once_the_answer_is_recorded()
    {
        using var temp = new TemporaryHome();
        var invalid = new ScriptEntry("""{"verdict":"maybe"}""", "/transcripts/1.jsonl", Scripts.Usage(1));
        var executor = new GatedExecutor(BuildScript(invalid, Scripts.Answer("done", 2))) { FreeCollects = 1 };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await PublishSingleAsync(daemon, Project);
        using var owner = new DaemonClient(temp.Home, sessionToken: null);
        FlowView flow = await StartSingleAsync(owner, Project, "Retried");

        // The first collect passes and is invalid, so the session is resumed and its second collect held.
        await executor.Holding.WaitAsync(Patience);
        executor.Inner.Resumes.Should().ContainSingle();
        SessionAccess access = AccessOf(executor, flow.Id);
        (await StatusAsync(daemon, access.Token, HttpMethod.Get, $"/flows?project={Project}", null)).Should().Be(HttpStatusCode.OK);

        executor.Open();
        await Flowing.UntilAsync(owner, Project, flow.Id, "ended");

        (await StatusAsync(daemon, access.Token, HttpMethod.Get, $"/flows?project={Project}", null)).Should().Be(HttpStatusCode.Unauthorized);
        executor.Inner.Spawns.Should().ContainSingle("the retry resumes the session; it is not a launch");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_token_stops_resolving_once_another_pauses_its_flow_and_a_relaunch_on_the_same_visit_gets_one_of_its_own_until_a_stop()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(BuildScript(Scripts.Answer("done", 1)));
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await PublishSingleAsync(daemon, Project);
        using var owner = new DaemonClient(temp.Home, sessionToken: null);
        FlowView flow = await StartSingleAsync(owner, Project, "Relaunched");
        await executor.Collecting.WaitAsync(Patience);

        string first = AccessOf(executor, flow.Id).Token;
        Guid firstLaunch = await LaunchOfAsync(daemon, Project, flow.Id);
        Principal early = await daemon.Services.GetRequiredService<SessionTokens>().ResolveAsync(first, None)
            ?? throw new InvalidOperationException("The first launch's token did not resolve while it ran.");

        await Flowing.PauseAsync(owner, Project, flow.Id, "A person pauses it.");
        (await StatusAsync(daemon, first, HttpMethod.Get, $"/flows?project={Project}", null)).Should().Be(HttpStatusCode.Unauthorized);

        // A write whose token resolved before the launch ended is refused on the flow as it now stands.
        FlowService flows = daemon.Services.GetRequiredService<FlowService>();
        int recorded = (await EventsAsync(daemon, Project, flow.Id)).Count;
        await FluentActions.Awaiting(() => flows.StartStepAsync(early, Project, flow.Id, "build", None))
            .Should().ThrowAsync<DomainException>()
            .WithMessage($"A session's principal records only on flow {Ids.Short(flow.Id)} in project '{Project}'*; that launch has ended, so it records nothing more.");
        (await EventsAsync(daemon, Project, flow.Id)).Should().HaveCount(recorded);

        // The gate stays shut, so the relaunch's collect is held as the first one was.
        await Flowing.UntilAsync(() => executor.Inner.Stops.Count == 1, "the paused judgment's session is stopped");
        await Flowing.ResumeAsync(owner, Project, flow.Id, "Go on.");
        await Flowing.UntilAsync(() => executor.Inner.Spawns.Count == 2, "the resumed judgment launches again");
        await Flowing.UntilAsync(
            async () => await LaunchOfAsync(daemon, Project, flow.Id) != firstLaunch && (await Flowing.ShowAsync(owner, Project, flow.Id)).Status == "running",
            "the relaunch is recorded");

        string second = executor.Inner.Spawns[1].Access?.Token ?? throw new InvalidOperationException("The relaunch carried no session access.");
        second.Should().NotBe(first);
        await Flowing.UntilAsync(
            async () => await StatusAsync(daemon, second, HttpMethod.Get, $"/flows?project={Project}", null) == HttpStatusCode.OK,
            "the relaunch's token resolves");
        (await StatusAsync(daemon, first, HttpMethod.Get, $"/flows?project={Project}", null)).Should().Be(HttpStatusCode.Unauthorized, "a token that stopped resolving never resolves again");

        await Flowing.StopAsync(owner, Project, flow.Id, "Not needed any more.");
        (await StatusAsync(daemon, second, HttpMethod.Get, $"/flows?project={Project}", null)).Should().Be(HttpStatusCode.Unauthorized);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_token_the_daemon_never_issued_is_refused_with_401()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);

        (await StatusAsync(daemon, "not-a-token-this-daemon-issued", HttpMethod.Get, "/projects", null)).Should().Be(HttpStatusCode.Unauthorized);
        (await StatusAsync(daemon, null, HttpMethod.Get, "/projects", null)).Should().Be(HttpStatusCode.Unauthorized);
        (await StatusAsync(daemon, daemon.Info.Token, HttpMethod.Get, "/projects", null)).Should().Be(HttpStatusCode.OK);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_client_holding_a_session_token_starts_no_daemon_when_none_answers()
    {
        using var temp = new TemporaryHome();
        using var session = new DaemonClient(temp.Home, "a-session-token");

        await FluentActions.Awaiting(() => session.GetAsync<List<FlowView>>($"/flows?project={Project}", None))
            .Should().ThrowAsync<DomainException>().WithMessage("The daemon that started this session is not running*");

        File.Exists(temp.Home.DaemonInfoPath).Should().BeFalse();
        File.Exists(temp.Home.LogPath).Should().BeFalse("no detached daemon was started to write its log");
    }

    [Fact]
    public void A_client_presents_the_session_token_only_to_the_home_whose_daemon_issued_it()
    {
        using var temp = new TemporaryHome();
        string other = Path.Combine(temp.Home.Path, "elsewhere");

        DaemonClient.SessionTokenFor(temp.Home, "a-session-token", temp.Home.Path).Should().Be("a-session-token");
        DaemonClient.SessionTokenFor(temp.Home, "a-session-token", temp.Home.Path + Path.DirectorySeparatorChar).Should().Be("a-session-token");
        DaemonClient.SessionTokenFor(temp.Home with { Path = other }, "a-session-token", temp.Home.Path).Should().BeNull("another home's daemon did not issue the token");
        DaemonClient.SessionTokenFor(temp.Home, "a-session-token", null).Should().BeNull("a token that names no home is sent nowhere");
        DaemonClient.SessionTokenFor(temp.Home, null, temp.Home.Path).Should().BeNull();
    }

    [Fact]
    public async Task A_client_for_another_home_reaches_its_daemon_with_that_daemons_own_token()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        string? token = DaemonClient.SessionTokenFor(temp.Home, "a-session-token", Path.Combine(temp.Home.Path, "the-session-home"));
        using var client = new DaemonClient(temp.Home, token);

        List<ProjectView> projects = await client.GetAsync<List<ProjectView>>("/projects", None);

        projects.Should().NotBeNull("the call carried the daemon token, not a token this daemon never issued");
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_launchs_token_is_stored_by_its_hash_before_its_session_starts_and_its_plain_value_is_nowhere_in_the_database()
    {
        using var temp = new TemporaryHome();
        IDocumentStore? daemonStore = null;
        SessionTokenRecord? storedAtSpawn = null;
        var executor = new GatedExecutor(BuildScript(Scripts.Answer("done", 1)))
        {
            BeforeSpawn = async access =>
            {
                if (access is not null && daemonStore is not null)
                {
                    await using IQuerySession query = daemonStore.QuerySession(Tenants.Node);
                    storedAtSpawn = await query.LoadAsync<SessionTokenRecord>(HashOf(access.Token), None);
                }
            },
        };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        daemonStore = daemon.Services.GetRequiredService<IDocumentStore>();
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await PublishSingleAsync(daemon, Project);
        using var owner = new DaemonClient(temp.Home, sessionToken: null);
        FlowView flow = await StartSingleAsync(owner, Project, "Stored by its hash");
        await executor.Collecting.WaitAsync(Patience);

        SessionAccess access = AccessOf(executor, flow.Id);
        Guid launch = await LaunchOfAsync(daemon, Project, flow.Id);
        storedAtSpawn.Should().NotBeNull("the binding is saved before the session's process starts");
        storedAtSpawn?.Project.Should().Be(Project);
        storedAtSpawn?.FlowId.Should().Be(flow.Id);
        storedAtSpawn?.LaunchId.Should().Be(launch);

        // Read once the daemon has stopped, so no write holds a lock on the files; a daemon stop keeps
        // the binding. The hash is on disk, which shows these are the files the store writes; the
        // plain token is not.
        await daemon.StopAsync(None);
        byte[] plain = Encoding.UTF8.GetBytes(access.Token);
        byte[] hash = Encoding.UTF8.GetBytes(HashOf(access.Token));
        byte[][] files = [.. new[] { temp.Home.DatabasePath }.Concat(temp.Home.DatabaseCompanionPaths).Where(File.Exists).Select(ReadShared)];
        files.Any(bytes => bytes.AsSpan().IndexOf(hash) >= 0).Should().BeTrue("the binding is stored by the token's hash");
        foreach (byte[] bytes in files)
        {
            bytes.AsSpan().IndexOf(plain).Should().Be(-1, "the token's plain value is never written to the store");
        }
    }

    [Fact]
    public async Task A_token_issued_before_a_restart_resolves_on_the_reattached_launch_under_the_same_rule_and_stops_once_its_answer_is_recorded()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(BuildScript(Scripts.Answer("done", 1), Scripts.Answer("done", 2), Scripts.Answer("done", 3)));
        FlowView own;
        FlowView sibling;
        FlowView elsewhere;
        SessionAccess access;
        Guid launch;
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = stalled }, None))
        {
            await RegisteredProjects.AddAsync(first, Project, None);
            await RegisteredProjects.AddAsync(first, Other, None);
            await PublishSingleAsync(first, Project);
            await PublishSingleAsync(first, Other);
            using var starter = new DaemonClient(temp.Home, sessionToken: null);
            own = await StartSingleAsync(starter, Project, "Its own");
            sibling = await StartSingleAsync(starter, Project, "A sibling");
            elsewhere = await StartSingleAsync(starter, Other, "Elsewhere");
            await Flowing.UntilAsync(() => stalled.Inner.Spawns.Count == 3, "every flow has launched its judgment");
            await SettledAsync(first, Project, own.Id);
            await SettledAsync(first, Project, sibling.Id);
            await SettledAsync(first, Other, elsewhere.Id);
            access = AccessOf(stalled, own.Id);
            launch = await LaunchOfAsync(first, Project, own.Id);
            await first.StopAsync(None);
        }

        stalled.Inner.Stops.Should().BeEmpty("a daemon stop leaves every session for a reattach");
        var binding = new SessionBinding(Project, own.Id, launch);
        string rule = $"A session's principal records only on flow {Ids.Short(own.Id)} in project '{Project}'*";

        var held = new GatedExecutor(stalled.Inner);
        await using (RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = held }, None))
        {
            await Flowing.UntilAsync(() => held.Inner.Reattaches.Count == 3, "every flow has reattached its session");
            using var owner = new DaemonClient(temp.Home, sessionToken: null);
            using var session = new DaemonClient(temp.Home, access.Token);

            // The token resolves to the same principal it did before the restart, and records on its own flow.
            Principal principal = await second.Services.GetRequiredService<SessionTokens>().ResolveAsync(access.Token, None)
                ?? throw new InvalidOperationException("The token issued before the restart did not resolve after it.");
            principal.Should().Be(Principal.ForSession(binding));
            (await StatusAsync(second, access.Token, HttpMethod.Get, $"/flows?project={Project}", null)).Should().Be(HttpStatusCode.OK);
            FlowService flows = second.Services.GetRequiredService<FlowService>();
            Guid sessionId = held.Inner.Spawns.Single(s => s.Access == access).Handle.Id;
            await flows.RecordUsageAsync(principal, Project, own.Id, "build", "build", sessionId, Scripts.Usage(1), None);
            (await EventsAsync(second, Project, own.Id)).OfType<JudgmentUsageRecorded>().Should().ContainSingle();

            // A reattach records nothing, and every other flow recorded its session before the stop.
            await SettledAsync(second, Project, sibling.Id);
            await SettledAsync(second, Other, elsewhere.Id);
            int siblingEvents = (await EventsAsync(second, Project, sibling.Id)).Count;
            int elsewhereEvents = (await EventsAsync(second, Other, elsewhere.Id)).Count;

            await FluentActions.Awaiting(() => Flowing.PauseAsync(session, Project, sibling.Id, "Not mine."))
                .Should().ThrowAsync<DomainException>().WithMessage(rule);
            await FluentActions.Awaiting(() => Flowing.PauseAsync(session, Other, elsewhere.Id, "Not mine either."))
                .Should().ThrowAsync<DomainException>().WithMessage(rule);
            ParcelView spare = await owner.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Never started", null), None);
            await FluentActions.Awaiting(() => Flowing.StartAsync(session, Project, spare.Id, Single))
                .Should().ThrowAsync<DomainException>().WithMessage(rule + "a flow it starts is never its own*");
            (await StatusAsync(second, access.Token, HttpMethod.Post, $"/flows/{sibling.Id}/pause", new FlowPauseRequest(Project, "Not mine.")))
                .Should().Be(HttpStatusCode.UnprocessableEntity);

            (await EventsAsync(second, Project, sibling.Id)).Should().HaveCount(siblingEvents, "a refused write appends nothing");
            (await EventsAsync(second, Other, elsewhere.Id)).Should().HaveCount(elsewhereEvents, "a refused write appends nothing");
            (await Flowing.OfParcelAsync(owner, Project, spare.Id)).Should().BeEmpty("a refused start starts nothing");

            await held.Holding.WaitAsync(Patience);
            await second.StopAsync(None);
        }

        // A further stop and start during the reattached collect leaves the token resolving.
        var answering = new GatedExecutor(stalled.Inner);
        await using RunningDaemon third = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = answering }, None);
        await answering.Holding.WaitAsync(Patience);
        (await third.Services.GetRequiredService<SessionTokens>().ResolveAsync(access.Token, None)).Should().Be(Principal.ForSession(binding));
        (await StatusAsync(third, access.Token, HttpMethod.Get, $"/flows?project={Project}", null)).Should().Be(HttpStatusCode.OK);

        answering.Open();
        using var client = new DaemonClient(temp.Home, sessionToken: null);
        await Flowing.UntilAsync(client, Project, own.Id, "ended");

        (await StatusAsync(third, access.Token, HttpMethod.Get, $"/flows?project={Project}", null)).Should().Be(HttpStatusCode.Unauthorized);
        await Flowing.UntilAsync(
            async () => (await BindingsOfAsync(third, launch)).Count == 0,
            "the ended judgment's bindings are removed");
        answering.Inner.Spawns.Should().HaveCount(3, "no flow launched its judgment again");

        await third.StopAsync(None);
    }

    [Fact]
    public async Task A_token_whose_session_a_restart_finds_lost_gets_401_and_its_binding_is_removed_and_stays_refused_after_a_resume()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(BuildScript(Scripts.Answer("done", 1)));
        (Guid flowId, SessionAccess access, Guid launch) = await StopDuringCollectAsync(temp, stalled, "Lost across the restart");

        var fresh = new GatedExecutor(BuildScript(Scripts.Answer("done", 1)));
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = fresh }, None);
        using var owner = new DaemonClient(temp.Home, sessionToken: null);
        FlowView paused = await Flowing.UntilAsync(owner, Project, flowId, "paused");
        paused.PauseReason.Should().StartWith("Step 'build' lost the session agent 'build' was launched in across a daemon restart");

        (await StatusAsync(second, access.Token, HttpMethod.Get, $"/flows?project={Project}", null)).Should().Be(HttpStatusCode.Unauthorized);
        await Flowing.UntilAsync(
            async () => (await BindingsOfAsync(second, launch)).Count == 0,
            "the lost launch's binding is removed");

        await Flowing.ResumeAsync(owner, Project, flowId, "Perform it again.");
        await fresh.Holding.WaitAsync(Patience);
        string relaunched = fresh.Inner.Spawns.Should().ContainSingle().Subject.Access?.Token
            ?? throw new InvalidOperationException("The relaunch carried no session access.");
        (await StatusAsync(second, relaunched, HttpMethod.Get, $"/flows?project={Project}", null)).Should().Be(HttpStatusCode.OK);
        (await StatusAsync(second, access.Token, HttpMethod.Get, $"/flows?project={Project}", null))
            .Should().Be(HttpStatusCode.Unauthorized, "a token that stopped resolving never resolves again");
        (await BindingsOfAsync(second, launch)).Should().BeEmpty();

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_binding_kept_by_a_daemon_stop_whose_flow_was_paused_while_no_daemon_ran_is_removed_at_the_next_start()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(BuildScript(Scripts.Answer("done", 1)));
        (Guid flowId, SessionAccess access, Guid launch) = await StopDuringCollectAsync(temp, stalled, "Paused while stopped");

        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            (await BindingsOfAsync(store, launch)).Should().ContainSingle("a daemon stop keeps the binding")
                .Which.Id.Should().Be(HashOf(access.Token));
            await new FlowService(store, TimeProvider.System).PauseAsync(
                Principal.LocalOwner, Project, flowId, "A person paused it.", FlowLevers.PerformAgain("build"), None);
        }

        var held = new GatedExecutor(stalled.Inner);
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = held }, None);

        (await BindingsOfAsync(second, launch)).Should().BeEmpty("the start removes a binding whose launch its flow no longer expects");
        (await StatusAsync(second, access.Token, HttpMethod.Get, $"/flows?project={Project}", null)).Should().Be(HttpStatusCode.Unauthorized);

        await second.StopAsync(None);
    }

    /// <summary>
    /// Starts a daemon, launches the one judgment of a flow on <paramref name="stalled"/>, and stops
    /// the daemon while that judgment's collect is held, once the flow has recorded its session.
    /// </summary>
    private static async Task<(Guid FlowId, SessionAccess Access, Guid Launch)> StopDuringCollectAsync(
        TemporaryHome temp, GatedExecutor stalled, string title)
    {
        await using RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = stalled }, None);
        await RegisteredProjects.AddAsync(first, Project, None);
        await PublishSingleAsync(first, Project);
        using var owner = new DaemonClient(temp.Home, sessionToken: null);
        FlowView flow = await StartSingleAsync(owner, Project, title);
        await stalled.Holding.WaitAsync(Patience);
        await SettledAsync(first, Project, flow.Id);
        SessionAccess access = AccessOf(stalled, flow.Id);
        Guid launch = await LaunchOfAsync(first, Project, flow.Id);
        await first.StopAsync(None);
        stalled.Inner.Stops.Should().BeEmpty("a daemon stop leaves the session for a reattach");
        return (flow.Id, access, launch);
    }

    private static Task<IReadOnlyList<SessionTokenRecord>> BindingsOfAsync(RunningDaemon daemon, Guid launch) =>
        BindingsOfAsync(daemon.Services.GetRequiredService<IDocumentStore>(), launch);

    private static async Task<IReadOnlyList<SessionTokenRecord>> BindingsOfAsync(IDocumentStore store, Guid launch)
    {
        await using IQuerySession query = store.QuerySession(Tenants.Node);
        return await query.Query<SessionTokenRecord>().Where(r => r.LaunchId == launch).ToListAsync(None);
    }

    private static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static byte[] ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        file.CopyTo(copy);
        return copy.ToArray();
    }

    private static ScriptedExecutor BuildScript(params ScriptEntry[] entries) =>
        new(new Dictionary<string, IReadOnlyList<ScriptEntry>> { ["build"] = entries });

    private static Task<Contour> PublishSingleAsync(RunningDaemon daemon, string project) =>
        Publishing.ContourAsync(daemon, project, new Contour
        {
            Name = Single,
            Accepts = ["task"],
            Outcome = "built",
            Budget = 10m,
            SpawnCap = 3,
            Steps =
            [
                new ContourStep
                {
                    Name = "build",
                    Agent = "build",
                    AnswerSchema = "build",
                    Transitions = [new ContourTransition { Field = "verdict", Value = ContourValue.Of("done"), VisitCap = 1, Target = Contour.End }],
                },
            ],
        });

    private static async Task<FlowView> StartSingleAsync(DaemonClient client, string project, string title)
    {
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(project, "task", title, null), None);
        return await Flowing.StartAsync(client, project, parcel.Id, Single);
    }

    /// <summary>The session access the flow's one launch was spawned with, found by the flow's working directory.</summary>
    private static SessionAccess AccessOf(GatedExecutor executor, Guid flowId) =>
        executor.Inner.Spawns.Should().ContainSingle(s => s.WorkingDirectory.EndsWith(flowId.ToString(), StringComparison.Ordinal))
            .Which.Access.Should().NotBeNull().And.Subject.As<SessionAccess>();

    private static async Task<Guid> LaunchOfAsync(RunningDaemon daemon, string project, Guid flowId)
    {
        Flow? flow = await daemon.Services.GetRequiredService<FlowService>().ShowAsync(Principal.LocalOwner, project, flowId, None);
        return flow?.Current?.LaunchId ?? Guid.Empty;
    }

    private static async Task<IReadOnlyList<object>> EventsAsync(RunningDaemon daemon, string project, Guid flowId)
    {
        await using IQuerySession session = daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(project);
        var events = await session.Events.FetchStreamAsync(flowId, token: None);
        return [.. events.Select(e => e.Data)];
    }

    /// <summary>
    /// Waits until the flow has recorded its session, after which its runner appends nothing more
    /// while the gate stays closed.
    /// </summary>
    private static Task SettledAsync(RunningDaemon daemon, string project, Guid flowId) =>
        Flowing.UntilAsync(
            async () => (await EventsAsync(daemon, project, flowId)).OfType<JudgmentSessionOpened>().Any(),
            $"flow {Ids.Short(flowId)} in project '{project}' has recorded its session");

    private static HttpClient Http(RunningDaemon daemon) => new() { BaseAddress = new Uri(daemon.Info.BaseAddress) };

    /// <summary>The status a call presenting <paramref name="token"/>, or no token, is answered with.</summary>
    private static async Task<HttpStatusCode> StatusAsync(RunningDaemon daemon, string? token, HttpMethod method, string path, object? body)
    {
        using HttpClient http = Http(daemon);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        if (token is not null)
        {
            request.Headers.Add(DaemonHost.TokenHeader, token);
        }

        using HttpResponseMessage response = await http.SendAsync(request, None);
        return response.StatusCode;
    }
}
