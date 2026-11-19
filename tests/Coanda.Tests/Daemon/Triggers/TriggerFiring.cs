using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Core;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events;

namespace Coanda.Tests.Daemon.Triggers;

/// <summary>Where a parcel's claims leave this node when the trigger loop weighs them.</summary>
public enum Standing
{
    Held,
    Unclaimed,
    Lost,
}

/// <summary>
/// What the trigger definition tests share: flows that pause at a person's step at once, so a flow run a
/// trigger starts stays where a test can see it and spawns nothing; triggers to publish; and a node seeded
/// before its daemon starts, with the claims, the imported lines, and a marker event whose parcel says the
/// loop has handled every entry before it. A peer's records are stood in by giving an event the record the
/// import keeps for a line of another origin.
/// </summary>
public static class TriggerFiring
{
    public const string Project = "alpha";

    /// <summary>The event kind of <see cref="Marker"/>, which nothing else matches.</summary>
    public const string MarkerKind = "marker";

    public static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A flow on a task that pauses for a person at once.</summary>
    public static readonly Flow Hold = Waiting("hold", "approved");

    /// <summary>A flow whose flow runs a test ends by hand, so its end is what triggers and follows read.</summary>
    public static readonly Flow Source = Waiting("source", "approved");

    /// <summary>A flow following <see cref="Source"/>'s end on the same parcel.</summary>
    public static readonly Flow AfterSource = Waiting("after-source", "approved") with
    {
        Follow = new FlowFollow { Outcome = "approved", Flow = "source", DepthCap = 1 },
    };

    /// <summary>A flow whose end closes its parcel.</summary>
    public static readonly Flow Closing = Waiting("closing", "approved") with { Closes = "completed" };

    /// <summary>A trigger creating a task titled by the marker event's title, which tells a test the loop has read that far.</summary>
    public static readonly TriggerDefinition Marker = Creating("marker", MarkerKind, Fields());

    public static Flow Waiting(string name, string outcome) => new()
    {
        Name = name,
        Accepts = ["task"],
        Outcome = outcome,
        Budget = 1m,
        SpawnCap = 1,
        Steps =
        [
            new FlowStep
            {
                Name = "approve",
                SystemStep = CoreSystemSteps.WaitForHuman,
                Arguments = new Dictionary<string, JsonElement> { ["lever"] = FlowValue.Of("Resume to approve.") },
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    public static IReadOnlyDictionary<string, string> Fields(params (string Name, string Value)[] fields) =>
        fields.ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);

    /// <summary>A trigger whose target creates a parcel of <paramref name="type"/> titled by <paramref name="titleField"/> and starts <paramref name="flow"/> on it.</summary>
    public static TriggerDefinition Creating(
        string name, string kind, IReadOnlyDictionary<string, string> filters, string flow = "hold", string type = "task", string titleField = "title") => new()
        {
            Name = name,
            EventKind = kind,
            Filters = filters,
            Target = new TriggerTarget { CreateParcel = new CreateParcelTarget { ParcelType = type, TitleField = titleField, Flow = flow } },
            HourlyCap = 100,
        };

    /// <summary>A trigger whose target starts <paramref name="flow"/> on the parcel the event names.</summary>
    public static TriggerDefinition OnEventParcel(string name, string kind, IReadOnlyDictionary<string, string> filters, string flow = "hold") => new()
    {
        Name = name,
        EventKind = kind,
        Filters = filters,
        Target = new TriggerTarget { EventParcel = new EventParcelTarget { Flow = flow } },
    };

    /// <summary>A trigger on the end of a flow run of <paramref name="followed"/>, creating a task titled by the ended parcel's title.</summary>
    public static TriggerDefinition OnEnd(string name, string followed, string flow = "hold", string type = "task", string titleField = "title") =>
        Creating(name, TriggerEvent.FlowRunEndedKind, Fields(("flow", followed), ("outcome", "approved")), flow, type, titleField);

    /// <summary>Drafts <paramref name="trigger"/> in <paramref name="scope"/>, in <see cref="Project"/> when that is project scope, and publishes it unless <paramref name="publish"/> is false.</summary>
    public static async Task<Definition> TriggerAsync(IDocumentStore store, TriggerDefinition trigger, DefinitionScope scope = DefinitionScope.Project, bool publish = true)
    {
        DefinitionService definitions = DefinitionsOf(store);
        string? project = scope == DefinitionScope.Project ? Project : null;
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Trigger, scope, project, trigger.Name, TriggerDefinition.CanonicalJson(trigger), trigger.Hash, null, None);
        return publish
            ? await definitions.PublishAsync(Principal.LocalOwner, project, draft.Id, None)
            : draft;
    }

    /// <summary>A definition service on <paramref name="store"/> whose flows may name the core's and the software module's system steps.</summary>
    public static DefinitionService DefinitionsOf(IDocumentStore store) => new(store, TimeProvider.System, systemSteps: PreviousFlows.SystemSteps);

    /// <summary>Publishes <paramref name="flow"/> as a project-scope flow of <see cref="Project"/>.</summary>
    public static async Task FlowAsync(IDocumentStore store, Flow flow)
    {
        DefinitionService definitions = DefinitionsOf(store);
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, Project, flow.Name, Flow.CanonicalJson(flow), flow.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
    }

    /// <summary>
    /// Starts and stops a daemon on the home, so its trigger position is fixed, then registers <see cref="Project"/>,
    /// publishes <see cref="Hold"/>, <see cref="Source"/>, <see cref="AfterSource"/>, <see cref="Closing"/>, and
    /// <see cref="Marker"/>, and writes what <paramref name="seed"/> does, all of which the next daemon's loop reads.
    /// </summary>
    public static async Task SeedAsync(TemporaryHome temp, Func<Seeded, Task> seed)
    {
        NodeIdentity identity = await NodeIdentity.EnsureAsync(temp.Home, None);
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, None))
        {
            await first.StopAsync(None);
        }

        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, None);
        foreach (Flow flow in new[] { Hold, Source, AfterSource, Closing })
        {
            await FlowAsync(store, flow);
        }

        await TriggerAsync(store, Marker);
        await using (IDocumentSession node = store.LightweightSession(Tenants.Node))
        {
            node.Store(new LedgerOrigin { NodeId = identity.NodeId });
            await node.SaveChangesAsync(None);
        }

        await seed(new Seeded(store, identity));
    }

    /// <summary>Records this node's marker event titled <paramref name="title"/>, whose parcel exists once the loop has read every entry before it.</summary>
    public static Task MarkAsync(IDocumentStore store, string title) =>
        new TriggerService(store, TimeProvider.System).RecordEventAsync(
            Principal.LocalOwner, Project, MarkerKind, $"marker/{title}", null, Fields(("title", title)), None);

    /// <summary>Waits for the parcel titled <paramref name="title"/>.</summary>
    public static async Task<ParcelView> ParcelTitledAsync(DaemonClient client, string title)
    {
        ParcelView? found = null;
        await FlowRunDriver.UntilAsync(
            async () =>
            {
                found = (await ParcelsAsync(client)).FirstOrDefault(p => p.Title == title);
                return found is not null;
            },
            $"a parcel titled '{title}' exists");
        return found ?? throw new InvalidOperationException("Unreachable: the wait returned without a parcel.");
    }

    public static async Task<List<ParcelView>> ParcelsAsync(DaemonClient client) =>
        await client.GetAsync<List<ParcelView>>($"/parcels?project={Project}", None);

    /// <summary>The parcels of the project other than the markers' and <paramref name="except"/>.</summary>
    public static async Task<List<ParcelView>> CreatedAsync(DaemonClient client, params Guid[] except) =>
        [.. (await ParcelsAsync(client)).Where(p => !p.Title.StartsWith("Marker", StringComparison.Ordinal) && !except.Contains(p.Id))];

    /// <summary>Gives every event of <paramref name="stream"/> past version <paramref name="after"/> the record the import keeps for a line of a peer.</summary>
    public static async Task ImportAsync(IDocumentStore store, Guid stream, long after = 0, Guid? peer = null)
    {
        await using IDocumentSession session = store.LightweightSession(Project);
        Guid origin = peer ?? Ids.New();
        foreach (IEvent entry in (await session.Events.FetchStreamAsync(stream, token: None)).Where(e => e.Version > after))
        {
            session.Store(new LedgerLine { Id = entry.Id, Origin = origin, Sequence = entry.Version, Hash = RandomHash() });
        }

        await session.SaveChangesAsync(None);
    }

    /// <summary>The version of <paramref name="stream"/>'s last event.</summary>
    public static async Task<long> VersionAsync(IDocumentStore store, Guid stream)
    {
        await using IQuerySession session = store.QuerySession(Project);
        return (await session.Events.FetchStreamAsync(stream, token: None))[^1].Version;
    }

    public static string RandomHash() => $"sha256:{Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())}{new string('0', 32)}";

    /// <summary>A parcel of <see cref="Project"/>, a task as a person adds it.</summary>
    public static async Task<Guid> TaskAsync(IDocumentStore store, string title) =>
        (await new ParcelService(store, TimeProvider.System).AddAsync(Principal.LocalOwner, Project, "task", title, null, None)).Id;

    /// <summary>A node's store while it is seeded, with the identity its daemon acts as.</summary>
    public sealed class Seeded(IDocumentStore store, NodeIdentity identity)
    {
        private long sequence;

        public IDocumentStore Store { get; } = store;

        public NodeIdentity Identity { get; } = identity;

        public ParcelService Parcels { get; } = new(store, TimeProvider.System);

        public FlowRunService FlowRuns { get; } = new(store, TimeProvider.System);

        public TriggerService Triggers { get; } = new(store, TimeProvider.System);

        /// <summary>Leaves <paramref name="parcelId"/> standing at <paramref name="standing"/> for this node.</summary>
        public Task StandAsync(Guid parcelId, Standing standing) => standing switch
        {
            Standing.Held => ClaimAsync(parcelId, fetched: true),
            Standing.Lost => LoseAsync(parcelId),
            _ => Task.CompletedTask,
        };

        /// <summary>
        /// This node's claim on the parcel, on a line of its own that was pushed, and, when <paramref name="fetched"/>,
        /// followed by a fetch that succeeded, so the parcel is held; otherwise the claim is not yet held. Gives the
        /// line's sequence, which a fetch through it makes the claim held.
        /// </summary>
        public async Task<long> ClaimAsync(Guid parcelId, bool fetched)
        {
            await Parcels.ClaimAsync(Principal.LocalOwner, Project, parcelId, Identity.NodeId, None);
            long line = ++sequence;
            await using (IDocumentSession session = Store.LightweightSession(Project))
            {
                IEvent claim = (await session.Events.FetchStreamAsync(parcelId, token: None)).Last(e => e.Data is ParcelClaimed);
                session.Store(new LedgerLine { Id = claim.Id, Origin = Identity.NodeId, Sequence = line, Hash = RandomHash() });
                await session.SaveChangesAsync(None);
            }

            await LedgerProgress.AdvanceAsync(Store, Project, LedgerProgress.PushedId, line, None);
            if (fetched)
            {
                await LedgerProgress.AdvanceAsync(Store, Project, LedgerProgress.FetchedId, line, None);
            }

            return line;
        }

        /// <summary>A peer's claim on the parcel, on the peer's line, which this node has none to weigh against, so the peer's claim wins.</summary>
        public async Task LoseAsync(Guid parcelId)
        {
            Guid peer = Ids.New();
            await using IDocumentSession session = Store.LightweightSession(Project);
            IEvent claim = session.Events.Append(parcelId, new ParcelClaimed(parcelId, peer, "peer", DateTimeOffset.UtcNow)).Events[^1];
            session.Store(new LedgerLine { Id = claim.Id, Origin = peer, Sequence = 1, Hash = RandomHash() });
            await session.SaveChangesAsync(None);
        }

        /// <summary>Starts <paramref name="flow"/> on the parcel and ends it with its outcome; gives the ended flow run's id.</summary>
        public async Task<Guid> EndedAsync(Guid parcelId, Flow flow)
        {
            Guid flowRun = (await FlowRuns.StartAsync(Principal.LocalOwner, Project, parcelId, flow.Name, None)).Id;
            await FlowRuns.EndAsync(Principal.LocalOwner, Project, flowRun, flow.Outcome, None);
            return flowRun;
        }

        /// <summary>Records an external event of this node's of <paramref name="kind"/>, naming <paramref name="parcelId"/> when given.</summary>
        public async Task<ExternalEvent> RecordAsync(string kind, IReadOnlyDictionary<string, string> fields, Guid? parcelId = null) =>
            await Triggers.RecordEventAsync(Principal.LocalOwner, Project, kind, $"{kind}/{Ids.New()}", parcelId, fields, None);
    }
}
