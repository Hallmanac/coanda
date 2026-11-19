using System.Net;
using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Triggers;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Sdk.Client;
using Coanda.Sdk.Contract;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// A module records an external event through the loopback API's <c>/events</c> route with the SDK
/// client's call, and gets back the event the project's record holds for that kind and external id.
/// The route refuses a body with a member missing or null before any rule reads it, and holds every
/// other caller to the registry and the kind rules.
/// </summary>
public sealed class EventRecordApiTests
{
    private const string On = ModuleNode.On;
    private const string Off = ModuleNode.Off;
    private const string Kind = "test/pushed";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Patience = TestPatience.Bound;

    [Fact]
    public async Task A_module_records_an_event_through_the_client_and_the_record_holds_exactly_it()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        Parcel parcel = await node.ParcelAsync(On);
        using var http = new HttpClient();
        CoreApiClient client = await ClientAsync(node, http);
        DateTimeOffset before = DateTimeOffset.UtcNow;

        CoreExternalEvent recorded = await client.RecordEventAsync(
            new RecordedEvent(On, Kind, "push-1", new Dictionary<string, string> { ["branch"] = "main", ["sha"] = "abc123" }, parcel.Id), None);

        Assert.NotEqual(Guid.Empty, recorded.Id);
        Assert.Equal(On, recorded.Project);
        Assert.Equal(Kind, recorded.Kind);
        Assert.Equal("push-1", recorded.ExternalId);
        Assert.Equal(parcel.Id, recorded.ParcelId);
        Assert.Equal(new Dictionary<string, string> { ["branch"] = "main", ["sha"] = "abc123" }, recorded.Fields);
        Assert.Equal("module:test", recorded.RecordedBy);
        Assert.InRange(recorded.RecordedAt, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        ExternalEvent held = Assert.Single(await RecordAsync(node, On));
        AssertHolds(recorded, held);

        CoreExternalEvent unparcelled = await client.RecordEventAsync(
            new RecordedEvent(On, Kind, "push-2", new Dictionary<string, string> { ["branch"] = "next" }), None);

        Assert.Null(unparcelled.ParcelId);
        Assert.Equal("push-2", unparcelled.ExternalId);
        ExternalEvent stored = Assert.Single(await RecordAsync(node, On), e => e.ExternalId == "push-2");
        Assert.Null(stored.ParcelId);
        AssertHolds(unparcelled, stored);
    }

    [Fact]
    public async Task Recording_the_same_kind_and_external_id_again_returns_the_first_event_whatever_else_it_gives()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        Parcel first = await node.ParcelAsync(On, "First");
        Parcel second = await node.ParcelAsync(On, "Second");
        using var http = new HttpClient();
        CoreApiClient client = await ClientAsync(node, http);

        CoreExternalEvent recorded = await client.RecordEventAsync(
            new RecordedEvent(On, Kind, "push-1", new Dictionary<string, string> { ["sha"] = "abc123" }, first.Id), None);
        CoreExternalEvent again = await client.RecordEventAsync(
            new RecordedEvent(On, Kind, "push-1", new Dictionary<string, string> { ["sha"] = "def456", ["extra"] = "x" }, second.Id), None);
        CoreExternalEvent unparcelled = await client.RecordEventAsync(
            new RecordedEvent(On, Kind, "push-1", new Dictionary<string, string>()), None);

        foreach (CoreExternalEvent returned in new[] { again, unparcelled })
        {
            Assert.Equal(recorded.Id, returned.Id);
            Assert.Equal(first.Id, returned.ParcelId);
            Assert.Equal(new Dictionary<string, string> { ["sha"] = "abc123" }, returned.Fields);
            Assert.Equal(recorded.RecordedAt, returned.RecordedAt);
        }

        AssertHolds(recorded, Assert.Single(await RecordAsync(node, On)));
    }

    [Fact]
    public async Task The_client_throws_the_cores_refusal_and_nothing_is_recorded()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        using var http = new HttpClient();
        CoreApiClient client = await ClientAsync(node, http);
        var fields = new Dictionary<string, string> { ["sha"] = "abc123" };

        CoreApiException offProject = await Assert.ThrowsAsync<CoreApiException>(
            () => client.RecordEventAsync(new RecordedEvent(Off, Kind, "push-1", fields), None));
        CoreApiException othersKind = await Assert.ThrowsAsync<CoreApiException>(
            () => client.RecordEventAsync(new RecordedEvent(On, "other/pushed", "push-1", fields), None));
        CoreApiException plainKind = await Assert.ThrowsAsync<CoreApiException>(
            () => client.RecordEventAsync(new RecordedEvent(On, "pushed", "push-1", fields), None));
        CoreApiException blankId = await Assert.ThrowsAsync<CoreApiException>(
            () => client.RecordEventAsync(new RecordedEvent(On, Kind, "  ", fields), None));

        Assert.All(new[] { offProject, othersKind, plainKind, blankId }, e => Assert.Equal(422, e.Status));
        Assert.Equal(ModuleNode.NotSwitchedOn(ModuleNode.Module, Off), offProject.Message);
        Assert.Equal(NotOwnKind("other/pushed"), othersKind.Message);
        Assert.Equal(NotOwnKind("pushed"), plainKind.Message);
        Assert.StartsWith(TriggerDecider.ExternalIdRule, blankId.Message, StringComparison.Ordinal);
        Assert.Empty(await RecordAsync(node, On));
        Assert.Empty(await RecordAsync(node, Off));
    }

    public static TheoryData<string, string> Incomplete()
    {
        var cases = new TheoryData<string, string>();
        foreach (string member in new[] { "project", "kind", "externalId", "fields" })
        {
            cases.Add(member, "missing");
            cases.Add(member, "null");
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Incomplete))]
    public async Task A_body_leaving_out_a_member_or_giving_it_as_null_is_refused_naming_it(string member, string how)
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        string token = await node.TokenAsync(ModuleNode.Module);
        Dictionary<string, object?> body = Whole();
        if (how == "missing")
        {
            body.Remove(member);
        }
        else
        {
            body[member] = null;
        }

        (HttpStatusCode status, string answer) = await node.SendAsync(token, HttpMethod.Post, EventEndpoints.Path, body);

        Assert.True((int)status is >= 400 and < 500, $"The request without its {member} was answered {status}: {answer}");
        Assert.Equal(
            $"An event to record needs its '{member}'; the request left it out or gave it as null.",
            JsonSerializer.Deserialize<ErrorView>(answer, JsonSerializerOptions.Web)?.Error);
        Assert.Empty(await RecordAsync(node, On));
    }

    [Fact]
    public async Task A_body_giving_a_field_as_null_is_refused_naming_the_field()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        string token = await node.TokenAsync(ModuleNode.Module);
        Dictionary<string, object?> body = Whole();
        body["fields"] = new Dictionary<string, string?> { ["sha"] = "abc123", ["branch"] = null };

        string refusal = await node.RefusedAsync(token, HttpMethod.Post, EventEndpoints.Path, body);

        Assert.Equal("The event's field 'branch' is null; every field of an event is a string.", refusal);
        Assert.Empty(await RecordAsync(node, On));
    }

    [Fact]
    public async Task The_daemon_token_records_a_plain_kind_as_the_owner_and_is_refused_a_module_prefixed_kind()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        using var owner = new DaemonClient(node.Home);
        string ownerId = node.Services.GetRequiredService<NodeOwner>().Id;

        ExternalEventView recorded = await owner.PostAsync<ExternalEventView>(
            EventEndpoints.Path, new RecordedEvent(On, "deployed", "deploy-1", new Dictionary<string, string> { ["env"] = "prod" }), None);
        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => owner.PostAsync<ExternalEventView>(EventEndpoints.Path, new RecordedEvent(On, Kind, "push-1", new Dictionary<string, string>()), None));

        Assert.Equal(ownerId, recorded.RecordedBy);
        Assert.Equal("deployed", recorded.Kind);
        Assert.Equal($"Only module 'test' records external events of kinds prefixed 'test/'; '{ownerId}' cannot record '{Kind}'.", refused.Message);
        ExternalEvent held = Assert.Single(await RecordAsync(node, On));
        Assert.Equal(recorded.Id, held.Id);
        Assert.Equal(ownerId, held.RecordedBy);
    }

    [Fact]
    public async Task A_session_token_is_refused_because_event_record_is_not_open_to_the_agent_role()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(new ScriptedExecutor(
            new Dictionary<string, IReadOnlyList<ScriptEntry>> { ["build"] = [Scripts.Answer("done", 1)] }));
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, On, None);
        await Publishing.FlowAsync(daemon, On, SingleAgentStep);
        using var owner = new DaemonClient(temp.Home, sessionToken: null);
        ParcelView parcel = await owner.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(On, "task", "Talks back", null), None);
        await FlowRunDriver.StartAsync(owner, On, parcel.Id, SingleAgentStep.Name);
        await executor.Collecting.WaitAsync(Patience);
        string token = Assert.Single(executor.Inner.Spawns).Access?.Token ?? throw new InvalidOperationException("The launch carried no session access.");
        using var session = new DaemonClient(temp.Home, token);

        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => session.PostAsync<ExternalEventView>(EventEndpoints.Path, new RecordedEvent(On, "deployed", "deploy-1", new Dictionary<string, string>()), None));

        Assert.Equal("'event.record' is not open to the Agent role.", refused.Message);
        await using IQuerySession query = daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(On);
        Assert.Empty(await query.Query<ExternalEvent>().ToListAsync(None));
    }

    private static readonly Flow SingleAgentStep = new()
    {
        Name = "one-agent-step",
        Accepts = ["task"],
        Outcome = "built",
        Budget = 10m,
        SpawnCap = 3,
        Steps =
        [
            new FlowStep
            {
                Name = "build",
                Agent = "build",
                AnswerSchema = "build",
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    private static Dictionary<string, object?> Whole() => new(StringComparer.Ordinal)
    {
        ["project"] = On,
        ["kind"] = Kind,
        ["externalId"] = "push-1",
        ["fields"] = new Dictionary<string, string> { ["sha"] = "abc123" },
    };

    private static string NotOwnKind(string kind) =>
        $"Module '{ModuleNode.Module}' records external events of kinds it prefixes with its own name, '{ModuleNode.Module}/<name>'; '{kind}' is not one.";

    private static async Task<CoreApiClient> ClientAsync(ModuleNode node, HttpClient http) =>
        new(http, new CoreApiOptions(new Uri(node.Daemon.Info.BaseAddress), await node.TokenAsync(ModuleNode.Module)));

    private static void AssertHolds(CoreExternalEvent recorded, ExternalEvent held)
    {
        Assert.Equal(recorded.Id, held.Id);
        Assert.Equal(recorded.Project, held.Project);
        Assert.Equal(recorded.Kind, held.Kind);
        Assert.Equal(recorded.ExternalId, held.ExternalId);
        Assert.Equal(recorded.ParcelId, held.ParcelId);
        Assert.Equal(recorded.Fields, held.Fields);
        Assert.Equal(recorded.RecordedBy, held.RecordedBy);
        Assert.Equal(recorded.RecordedAt, held.RecordedAt);
    }

    private static async Task<IReadOnlyList<ExternalEvent>> RecordAsync(ModuleNode node, string project)
    {
        await using IQuerySession session = node.Services.GetRequiredService<IDocumentStore>().QuerySession(project);
        return await session.Query<ExternalEvent>().ToListAsync(None);
    }
}
