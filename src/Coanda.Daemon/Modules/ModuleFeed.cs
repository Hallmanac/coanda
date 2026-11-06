using System.Text.Json;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Projects;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Coanda.Sdk.Contract;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NJsonSchema;
using NJsonSchema.Validation;

namespace Coanda.Daemon.Modules;

/// <summary>
/// The module feed: how a module learns what Coanda itself did. Each hosted module whose describe
/// answer subscribes to core event kinds has a feed of its own, which reads the record past that
/// module's position in global sequence order and delivers each event of a subscribed kind, from a
/// project the module is switched on for, through the module's host adapter, one event to a delivery.
/// </summary>
/// <remarks>
/// <para>
/// The rule of thumb (design section 14.4): work goes in flows, and the feed is for observing and
/// mirroring. If a person should see an effect in the parcel's history, it is a step the daemon
/// dispatches; a mirror of Coanda elsewhere, a message about the work, or tidying what a flow run left
/// on the node is a feed reaction. The feed lets something happen in the world that no flow run
/// records, and only this rule and review keep modules from doing work beside flows through it.
/// </para>
/// <para>
/// Each module's position is a document in the node tenant (<see cref="FeedPosition"/>), written only
/// after the module has handled the delivery it covers, so delivery is at least once and a module reacts
/// to an event it has seen before as it did the first time. On a node where a module's feed has never
/// run, its position is fixed at the end of the existing record before the daemon advances any live flow
/// run or advertises its address, so it is delivered nothing recorded before that and everything after.
/// </para>
/// <para>
/// A flow run's ended or stopped delivery is held until no worker advances that flow run and no person's
/// stop of it is being saved (<see cref="FlowRunWorkers"/>), so a module never acts on the end of a flow
/// run while one of its steps is still running in its work path or a dispatch it holds is still to be
/// told to stop. A held delivery delays the later deliveries to that module, which arrive in order, and
/// no other module's. A delivery to a module that is unconfigured on this node for the event's project is
/// skipped with a log line, and one the module fails, or that does not meet the module API's feed
/// delivery schema, is logged and passed over; neither stops that module's feed or another's.
/// </para>
/// <para>
/// A flow run's ended or stopped event that another node recorded, which the import appended from its lines
/// (<see cref="ImportedEvents"/>), is not delivered: what that flow run left behind is on its own node, and a
/// module here acting on it would tidy away what belongs to the flow runs of this node.
/// </para>
/// </remarks>
public sealed class ModuleFeed(
    IDocumentStore store,
    HostedModules modules,
    FlowRunWorkers workers,
    ModuleSettingsService settings,
    NodeIdentity identity,
    TimeProvider clock,
    ILogger<ModuleFeed> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private const int PageSize = 256;

    /// <summary>The module API's feed delivery schema, which the daemon's project embeds from <c>schemas/module/v1/</c>.</summary>
    private const string DeliverySchemaResource = "Coanda.Daemon.Modules.feed-delivery.schema.json";

    private static readonly Lazy<Task<JsonSchema>> DeliverySchema = new(ReadDeliverySchemaAsync);

    private readonly CancellationTokenSource stopping = new();
    private readonly List<Subscriber> subscribers = [];
    private Task running = Task.CompletedTask;

    /// <summary>
    /// Reads each subscribing module's position, or, for a module whose feed has never run on this node,
    /// writes the end of the existing record as its position. The daemon calls this once its hosted
    /// modules have described themselves, and before it advances any live flow run or writes its address file.
    /// </summary>
    public async Task FixPositionsAsync(CancellationToken cancellationToken)
    {
        foreach (string module in modules.Names.Order(StringComparer.Ordinal))
        {
            if (modules.DescribeOf(module) is not { Subscriptions.Count: > 0 } answer || modules.AdapterOf(module) is not { } adapter)
            {
                continue;
            }

            await using IDocumentSession session = store.LightweightSession(Tenants.Node);
            FeedPosition? stored = await session.LoadAsync<FeedPosition>(FeedPosition.IdOf(module), cancellationToken);
            long position;
            if (stored is not null)
            {
                position = stored.Sequence;
            }
            else
            {
                position = await store.Database.FetchHighestEventSequenceNumber(cancellationToken);
                session.Store(new FeedPosition { Id = FeedPosition.IdOf(module), Module = module, Sequence = position });
                await session.SaveChangesAsync(cancellationToken);
            }

            subscribers.Add(new Subscriber(module, adapter, answer.Subscriptions.ToHashSet(), position));
        }
    }

    /// <summary>Starts each subscribing module's feed from its fixed position, each on a worker of its own.</summary>
    public void Begin() => running = Task.WhenAll(subscribers.Select(s => Task.Run(() => RunAsync(s, stopping.Token))));

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.CancelAsync();
        try
        {
            await running.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The daemon stopped before the module feed finished the deliveries it was making.");
        }
    }

    public void Dispose() => stopping.Dispose();

    /// <summary>The feed event <paramref name="entry"/> is, or null when it is of no kind the feed delivers.</summary>
    private static FeedEvent? FeedEventOf(IEvent entry)
    {
        string project = entry.TenantId;
        long sequence = entry.Sequence;
        return entry.Data switch
        {
            ParcelCreated created => new FeedEvent.ParcelEntered(project, sequence, created.ParcelId, created.Stage),
            ParcelMoved moved => new FeedEvent.ParcelEntered(project, sequence, moved.ParcelId, moved.ToStage),
            FlowRunEnded ended => new FeedEvent.FlowRunEnded(project, sequence, ended.FlowRunId),
            FlowRunStopped stopped => new FeedEvent.FlowRunStopped(project, sequence, stopped.FlowRunId),
            FlowRunPaused paused => new FeedEvent.FlowRunPaused(project, sequence, paused.FlowRunId),
            ParcelFieldSet set => new FeedEvent.ParcelFieldSet(project, sequence, set.ParcelId, set.Field),
            _ => null,
        };
    }

    /// <summary>The kind a describe answer subscribes to <paramref name="feedEvent"/> by.</summary>
    private static CoreEventKind KindOf(FeedEvent feedEvent) => feedEvent switch
    {
        FeedEvent.ParcelEntered => CoreEventKind.ParcelEntered,
        FeedEvent.FlowRunEnded => CoreEventKind.FlowRunEnded,
        FeedEvent.FlowRunStopped => CoreEventKind.FlowRunStopped,
        FeedEvent.FlowRunPaused => CoreEventKind.FlowRunPaused,
        FeedEvent.ParcelFieldSet => CoreEventKind.ParcelFieldSet,
        _ => throw new InvalidOperationException($"A feed event of a kind the feed does not know: {feedEvent.GetType().Name}."),
    };

    private async Task RunAsync(Subscriber subscriber, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (await HandlePageAsync(subscriber, cancellationToken))
                {
                }

                await Task.Delay(PollInterval, clock, cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure)
            {
                // Reading the record failed; nothing past the position was delivered, so the next poll reads it again.
                logger.LogError(failure, "The feed of module '{Module}' could not read the record past sequence {Sequence}.", subscriber.Module, subscriber.Position);
                await Task.Delay(PollInterval, clock, CancellationToken.None);
            }
        }
    }

    /// <summary>Delivers what the next page of the record past the module's position holds for it; true when the position moved.</summary>
    private async Task<bool> HandlePageAsync(Subscriber subscriber, CancellationToken cancellationToken)
    {
        // The head is read before the projects are, so every event at or below it was committed
        // with its project's documents already in place: no project's first entry can slip past.
        long head = await store.Database.FetchHighestEventSequenceNumber(cancellationToken);
        if (head <= subscriber.Position)
        {
            return false;
        }

        long to = Math.Min(subscriber.Position + PageSize, head);
        foreach (IEvent entry in await ReadAsync(subscriber.Module, subscriber.Position, to, cancellationToken))
        {
            if (FeedEventOf(entry) is { } feedEvent && subscriber.Kinds.Contains(KindOf(feedEvent)))
            {
                await DeliverAsync(subscriber, feedEvent, cancellationToken);
                await SavePositionAsync(subscriber.Module, entry.Sequence, cancellationToken);
            }

            subscriber.Position = entry.Sequence;
        }

        // Sequences are contiguous, so whatever in the window no switched-on project holds is passed too.
        subscriber.Position = to;
        await SavePositionAsync(subscriber.Module, to, cancellationToken);
        return true;
    }

    /// <summary>
    /// The events with a sequence in (<paramref name="from"/>, <paramref name="to"/>] in every project
    /// <paramref name="module"/> is switched on for, in global sequence order, but another node's flow run ends
    /// and stops. A project is a tenant, and a read is a tenant's.
    /// </summary>
    private async Task<IReadOnlyList<IEvent>> ReadAsync(string module, long from, long to, CancellationToken cancellationToken)
    {
        IReadOnlyList<Project> projects;
        await using (IQuerySession query = store.QuerySession(Tenants.Node))
        {
            projects = await query.Query<Project>().AnyTenant().ToListAsync(cancellationToken);
        }

        List<IEvent> page = [];
        foreach (Project project in projects.Where(p => p.IsSwitchedOn(module)))
        {
            await using IQuerySession session = store.QuerySession(project.Name);
            IReadOnlyList<IEvent> events = await session.Events.QueryEventsAsync(e => e.Sequence > from && e.Sequence <= to, cancellationToken);
            IReadOnlySet<Guid> imported = await ImportedEvents.AmongAsync(
                session, identity.NodeId, [.. events.Where(e => e.Data is FlowRunEnded or FlowRunStopped).Select(e => e.Id)], cancellationToken);
            page.AddRange(events.Where(e => !imported.Contains(e.Id)));
        }

        return [.. page.OrderBy(e => e.Sequence)];
    }

    /// <summary>
    /// Delivers one event once it may be: a flow run's end or stop once its worker has returned and its
    /// stop has been handed on. A module unconfigured for the event's project is skipped, and a delivery
    /// that fails, the module's own handling included, is logged and passed over; a daemon stop is not a
    /// failure, and leaves the position where it was so the next start delivers the event again.
    /// </summary>
    private async Task DeliverAsync(Subscriber subscriber, FeedEvent feedEvent, CancellationToken cancellationToken)
    {
        try
        {
            Guid? final = feedEvent switch
            {
                FeedEvent.FlowRunEnded ended => ended.FlowRunId,
                FeedEvent.FlowRunStopped stopped => stopped.FlowRunId,
                _ => null,
            };
            if (final is Guid flowRunId)
            {
                await workers.ReturnedAsync(flowRunId, cancellationToken);
            }

            ModuleConfiguration configuration = await settings.StateAsync(feedEvent.Project, subscriber.Module, cancellationToken);
            if (configuration.State == ModuleState.Unconfigured)
            {
                logger.LogWarning(
                    "Skipped the feed delivery of sequence {Sequence} to module '{Module}', which is switched on for project '{Project}' and unconfigured on this node; it needs {Missing}.",
                    feedEvent.Sequence, subscriber.Module, feedEvent.Project, string.Join(", ", configuration.Missing));
                return;
            }

            var delivery = new FeedDelivery([feedEvent]);
            await RequireValidAsync(delivery, cancellationToken);
            await subscriber.Adapter.FeedAsync(delivery, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            logger.LogError(
                failure, "The feed delivery of sequence {Sequence} in project '{Project}' to module '{Module}' failed and is passed over.",
                feedEvent.Sequence, feedEvent.Project, subscriber.Module);
        }
    }

    /// <summary>Refuses a delivery that does not meet the module API's feed delivery schema, which nothing then sends.</summary>
    private static async Task RequireValidAsync(FeedDelivery delivery, CancellationToken cancellationToken)
    {
        JsonSchema schema = await DeliverySchema.Value.WaitAsync(cancellationToken);
        ICollection<ValidationError> failures = schema.Validate(JsonSerializer.Serialize(delivery, ModuleJson.Options));
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"The feed delivery breaks the module API's feed delivery schema: {string.Join("; ", failures.Select(f => $"{f.Kind} at {f.Path ?? "#"}"))}.");
        }
    }

    private async Task SavePositionAsync(string module, long sequence, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession(Tenants.Node);
        session.Store(new FeedPosition { Id = FeedPosition.IdOf(module), Module = module, Sequence = sequence });
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the embedded feed delivery schema once for the whole process. It takes no cancellation token
    /// on purpose: <see cref="DeliverySchema"/> caches the task it returns, so a token one caller cancelled
    /// would leave every later read with a cancelled schema.
    /// </summary>
    private static async Task<JsonSchema> ReadDeliverySchemaAsync()
    {
        await using Stream stream = typeof(ModuleFeed).Assembly.GetManifestResourceStream(DeliverySchemaResource)
            ?? throw new InvalidOperationException($"The daemon carries no '{DeliverySchemaResource}'.");
        return await JsonSchema.FromJsonAsync(stream, CancellationToken.None);
    }

    /// <summary>One module's feed: its adapter, the kinds it subscribes to, and how far it has read the record.</summary>
    private sealed class Subscriber(string module, ModuleHostAdapter adapter, IReadOnlySet<CoreEventKind> kinds, long position)
    {
        public string Module { get; } = module;

        public ModuleHostAdapter Adapter { get; } = adapter;

        public IReadOnlySet<CoreEventKind> Kinds { get; } = kinds;

        public long Position { get; set; } = position;
    }
}
