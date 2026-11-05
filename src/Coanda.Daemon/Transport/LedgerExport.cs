using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Types;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Repositories;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.Transport;

/// <summary>
/// The node's export of what it authored (design section 6): for each project, every event of the
/// project's tenant this node has not yet written, in store order, becomes the next signed line of the
/// node's chain in that project, recorded in the store as a <see cref="LedgerLine"/>, then written into
/// every clone of the project on <c>refs/coanda/nodes/&lt;node-id&gt;</c> and pushed. The node tenant is
/// never exported. An event the store records on another origin's line is never written as this node's.
/// </summary>
/// <remarks>
/// <para>
/// A line carries the event in this build's current shape, read through the store's upgrades: a
/// registration from before repos is written with its repo and none of the fields this build no longer
/// reads, and an event this build has no type for is not read at all, so it is never written. A fork base
/// in the earlier shape is resolved on this node to the module-scope definition of its kind it names, and
/// a project whose fork base cannot be resolved stops exporting before that line, every later round, until
/// it can.
/// </para>
/// <para>
/// A line's tag is read when it is written and signed with it, so it is kept with the line and never read
/// again: an event of a parcel's stream, or of one of its flow runs' streams, names the repos the parcel's
/// tag field holds, read from the field its type version marks, and a set of the tag field names those,
/// the ones the set wrote, and the ones the field held before the set. Every other event, and one of a parcel whose type version
/// marks no tag field or whose field holds nothing, is project-wide.
/// </para>
/// <para>
/// Every round runs under one turn, so the loop and <see cref="PublishAsync"/> never extend a chain at
/// once, and refuses everything when this node's id is not the one the store first exported under.
/// </para>
/// </remarks>
public sealed class LedgerExport(
    IDocumentStore store,
    NodeHome home,
    NodeIdentity identity,
    ICommandRunner runner,
    TimeProvider clock,
    ILogger<LedgerExport> logger) : IHostedService, IDisposable
{
    /// <summary>
    /// How often the daemon exports and pushes when <c>coa daemon run</c> starts it: an event reaches the
    /// remote within this long. Fifteen seconds keeps a busy flow run to four pushes a minute per repo, and
    /// a command that must publish before it answers calls <see cref="PublishAsync"/> instead of waiting.
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    private const int PageSize = 512;

    private readonly SemaphoreSlim turn = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly LedgerRefs refs = new(new Git(runner), identity.NodeId);

    /// <summary>
    /// How far each project's events have been read, past its last line when the events after that line
    /// were all passed over, so a quiet project is not read again from its last line every round. It is
    /// kept for the daemon's run alone: a start reads again from each project's last line, and passes over
    /// what is already on a line.
    /// </summary>
    private readonly Dictionary<string, long> read = new(StringComparer.Ordinal);

    private Task running = Task.CompletedTask;

    /// <summary>Whether the store is known to record this node's id as the one its lines are exported under.</summary>
    private bool originRecorded;

    /// <summary>The ref this node writes its ledger on in every clone.</summary>
    public string RefName => refs.RefName;

    /// <summary>Exports and pushes every project once, then again every <paramref name="interval"/>, on the daemon's own worker.</summary>
    public void Begin(TimeSpan interval) => running = Task.Run(() => RunAsync(interval, stopping.Token));

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
            logger.LogWarning("The daemon stopped before the ledger export finished its round.");
        }
    }

    public void Dispose()
    {
        stopping.Dispose();
        turn.Dispose();
    }

    /// <summary>Exports and pushes every project, each apart: one project's failure is logged and the others still export.</summary>
    public async Task<IReadOnlyList<LedgerPublication>> ExportAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Project> projects;
        await using (IQuerySession session = store.QuerySession(Tenants.Node))
        {
            projects = await session.Query<Project>().AnyTenant().ToListAsync(cancellationToken);
        }

        await turn.WaitAsync(cancellationToken);
        try
        {
            if (await RefusalAsync(cancellationToken) is { } refused)
            {
                return [.. projects.Select(p => new LedgerPublication(p.Name, 0, [refused]))];
            }

            List<LedgerPublication> published = [];
            foreach (Project project in projects.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                published.Add(await ExportAsync(project, cancellationToken));
            }

            return published;
        }
        finally
        {
            turn.Release();
        }
    }

    /// <summary>
    /// Exports and pushes <paramref name="project"/> now, for a command that must publish before it
    /// answers: what it gives says how many lines were written and every reason a line was not written or
    /// a repo not pushed.
    /// </summary>
    public async Task<LedgerPublication> PublishAsync(string project, CancellationToken cancellationToken)
    {
        string tenant = Tenants.RequireProject(project);
        Project found = await ProjectService.FindAsync(store, tenant, cancellationToken)
            ?? throw new DomainException($"No project '{tenant}' is registered on this node.");

        await turn.WaitAsync(cancellationToken);
        try
        {
            return await RefusalAsync(cancellationToken) is { } refused
                ? new LedgerPublication(found.Name, 0, [refused])
                : await ExportAsync(found, cancellationToken);
        }
        finally
        {
            turn.Release();
        }
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ExportAsync(cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure)
            {
                logger.LogError(failure, "The ledger export could not read the projects; it tries again in {Interval}.", interval);
            }

            try
            {
                await Task.Delay(interval, clock, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Why nothing may be exported, or null when this node may export: the store records the node id it
    /// first exported a line under (<see cref="RecordOriginAsync"/>), and a node whose id differs from it
    /// writes and pushes nothing. A store that has exported no line yet binds no id.
    /// </summary>
    private async Task<string?> RefusalAsync(CancellationToken cancellationToken)
    {
        if (originRecorded)
        {
            return null;
        }

        await using IQuerySession session = store.QuerySession(Tenants.Node);
        LedgerOrigin? origin = await session.LoadAsync<LedgerOrigin>(LedgerOrigin.DocumentId, cancellationToken);
        if (origin is null)
        {
            return null;
        }

        if (origin.NodeId == identity.NodeId)
        {
            originRecorded = true;
            return null;
        }

        string refused =
            $"This store exported its lines as node {origin.NodeId}, and this node is {identity.NodeId}; nothing is exported or pushed, since a second origin would sign the same history again. Carrying the earlier identity forward is the rebuild's.";
        logger.LogError("The ledger export refused every project: {Reason}", refused);
        return refused;
    }

    /// <summary>
    /// Records the store's node id as the one this node exports under, before its first line is saved, so a
    /// line is never in the store without it. Recorded once; <see cref="RefusalAsync"/> has already refused a
    /// node whose id differs from one recorded.
    /// </summary>
    private async Task RecordOriginAsync(CancellationToken cancellationToken)
    {
        if (originRecorded)
        {
            return;
        }

        await using IDocumentSession session = store.LightweightSession(Tenants.Node);
        switch (await session.LoadAsync<LedgerOrigin>(LedgerOrigin.DocumentId, cancellationToken))
        {
            case null:
                session.Store(new LedgerOrigin { NodeId = identity.NodeId });
                await session.SaveChangesAsync(cancellationToken);
                break;
            case { } origin when origin.NodeId != identity.NodeId:
                throw new InvalidOperationException(
                    $"This store records its lines as exported by node {origin.NodeId}, and this node is {identity.NodeId}, so no line is written.");
        }

        originRecorded = true;
    }

    /// <summary>
    /// Writes the project's chain into each of its clones and pushes it. The store's lines are read only when
    /// a clone's write or push needs them, and once a round for all the project's clones; a round whose chain
    /// every clone already holds and pushed reads none.
    /// </summary>
    private async Task<LedgerPublication> ExportAsync(Project project, CancellationToken cancellationToken)
    {
        List<string> failures = [];
        int written = 0;
        try
        {
            (written, LedgerLine? last) = await AppendAsync(project, failures, cancellationToken);
            if (last is not null)
            {
                Dictionary<long, LedgerSegments> loaded = [];
                async Task<LedgerSegments> SegmentsFromAsync(long from, CancellationToken token)
                {
                    if (!loaded.TryGetValue(from, out LedgerSegments? segments))
                    {
                        segments = LedgerSegments.Of(await LinesAsync(project.Name, from, token));
                        loaded[from] = segments;
                    }

                    return segments;
                }

                foreach (ProjectRepo repo in project.Repos)
                {
                    await WriteAndPushAsync(project, repo, last.Hash, SegmentsFromAsync, failures, cancellationToken);
                }
            }
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(failure, "The ledger export of project {Project} failed; the next round tries again.", project.Name);
            failures.Add(failure.Message);
        }

        return new LedgerPublication(project.Name, written, failures);
    }

    private async Task WriteAndPushAsync(
        Project project,
        ProjectRepo repo,
        string lastHash,
        Func<long, CancellationToken, Task<LedgerSegments>> segmentsFrom,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        string? clone = RepoLayout.IsPlain(project.Name) ? home.RepoClonePath(project.Name, repo.Name) : null;
        if (clone is null || !Directory.Exists(clone))
        {
            string missing = $"Repo '{repo.Name}' of project '{project.Name}' has no clone {(clone is null ? "under the node home, since the project's name is not a plain directory name" : $"at {clone}; the next start makes it")}, so its ledger is not written there.";
            logger.LogWarning("{Reason}", missing);
            failures.Add(missing);
            return;
        }

        try
        {
            await refs.WriteAndPushAsync(clone, lastHash, segmentsFrom, cancellationToken);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            string reason = $"The ledger of project '{project.Name}' was not written to or pushed from repo '{repo.Name}': {failure.Message}";
            logger.LogWarning("{Reason} The next round tries again.", reason);
            failures.Add(reason);
        }
    }

    /// <summary>This node's lines in the project from sequence <paramref name="from"/> on, in sequence order.</summary>
    private async Task<IReadOnlyList<LedgerLine>> LinesAsync(string tenant, long from, CancellationToken cancellationToken)
    {
        Guid origin = identity.NodeId;
        await using IQuerySession session = store.QuerySession(tenant);
        return await session.Query<LedgerLine>()
            .Where(l => l.Origin == origin && l.Sequence >= from)
            .OrderBy(l => l.Sequence)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Records the next lines of this node's chain in the project, page by page, each page in one save,
    /// and gives how many. A line that cannot be written stops the project's export before it, with its
    /// reason added to <paramref name="failures"/>; the lines before it are kept. It also gives this node's
    /// last line in the project, written now or before, or null when it has none.
    /// </summary>
    private async Task<(int Written, LedgerLine? Last)> AppendAsync(Project project, List<string> failures, CancellationToken cancellationToken)
    {
        string tenant = project.Name;
        Guid origin = identity.NodeId;
        int written = 0;
        await using IQuerySession query = store.QuerySession(tenant);
        LedgerLine? last = await query.Query<LedgerLine>()
            .Where(l => l.Origin == origin)
            .OrderByDescending(l => l.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        long head = await store.Database.FetchHighestEventSequenceNumber(cancellationToken);
        var tags = new ParcelTags(query, new DefinitionResolver(store), tenant);

        long from = Math.Max(last?.StoreSequence ?? 0, read.GetValueOrDefault(tenant));
        while (from < head)
        {
            long to = Math.Min(from + PageSize, head);
            long reached = from;
            IReadOnlyList<IEvent> page = await query.Events.QueryEventsAsync(e => e.Sequence > reached && e.Sequence <= to, cancellationToken);
            List<LedgerLine> recorded = [];
            string? stopped = null;
            long through = to;
            foreach (IEvent entry in page.OrderBy(e => e.Sequence))
            {
                // An event on a line already, this node's or another origin's, is never written again.
                if (await query.LoadAsync<LedgerLine>(entry.Id, cancellationToken) is not null)
                {
                    continue;
                }

                long sequence = (last?.Sequence ?? 0) + 1;
                object? data = await CurrentAsync(entry, cancellationToken);
                if (data is null)
                {
                    stopped = await UnresolvedAsync(project, sequence, (DefinitionDrafted)entry.Data, cancellationToken);
                    through = entry.Sequence - 1;
                    break;
                }

                LineTag tag = await tags.OfAsync(entry, cancellationToken);
                last = Line(project, entry, sequence, last, tag, new StoredEvent(entry.EventTypeName, store.Options.Serializer.ToJson(data)));
                recorded.Add(last);
            }

            if (recorded.Count > 0)
            {
                await RecordOriginAsync(cancellationToken);
                await using IDocumentSession session = store.LightweightSession(tenant);
                session.Store([.. recorded]);
                await session.SaveChangesAsync(cancellationToken);
                written += recorded.Count;
            }

            read[tenant] = through;
            if (stopped is not null)
            {
                logger.LogError("{Reason}", stopped);
                failures.Add(stopped);
                break;
            }

            from = to;
        }

        return (written, last);
    }

    private LedgerLine Line(Project project, IEvent entry, long sequence, LedgerLine? last, LineTag tag, StoredEvent current)
    {
        DateTimeOffset recordedAt = entry.Timestamp.ToUniversalTime();
        var content = new LineContent(project.Id, identity.NodeId, sequence, last?.Hash, tag, entry.StreamId, entry.Id, recordedAt, current);
        byte[] bytes = SignedLine.Sign(content, identity.Key);
        SignedLine signed = SignedLine.Parse(bytes, out string? fault)
            ?? throw new InvalidOperationException($"The line this node signed for event {entry.Id} does not parse: {fault}.");
        string month = recordedAt.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        return new LedgerLine
        {
            Id = entry.Id,
            Origin = identity.NodeId,
            Sequence = sequence,
            PreviousHash = last?.Hash,
            Tag = tag.RepoNames,
            Signature = signed.Signature,
            Hash = signed.Hash,
            Segment = last is not null && string.CompareOrdinal(last.Segment, month) > 0 ? last.Segment : month,
            Line = System.Text.Encoding.UTF8.GetString(bytes),
            StoreSequence = entry.Sequence,
        };
    }

    /// <summary>
    /// The event's data in this build's current shape, or null when it is a draft whose earlier-shape fork
    /// base no module-scope definition on this node matches. The store's read has already upgraded what
    /// <see cref="EarlierRecords"/> lists; a registration from before repos is given its repo and loses its
    /// remote, and an earlier-shape fork base is named by the base's name, module, module version, and
    /// content hash, leaving the draft's content and content hash as they are.
    /// </summary>
    private async Task<object?> CurrentAsync(IEvent entry, CancellationToken cancellationToken) =>
        entry.Data switch
        {
            ProjectRegistered { Remote: not null } registered => registered with { Repos = registered.ReposRecorded, Remote = null },
            DefinitionDrafted { ForkBase.IsEarlierShape: true } drafted =>
                await BaseOfAsync(drafted, cancellationToken) is { } resolved
                    ? drafted with { ForkBase = ForkBase.Of(resolved) }
                    : null,
            _ => entry.Data,
        };

    /// <summary>The module-scope definition of the draft's kind on this node that its fork base names, or null when none does.</summary>
    private async Task<Definition?> BaseOfAsync(DefinitionDrafted drafted, CancellationToken cancellationToken)
    {
        if (drafted.ForkBase is not { } forkBase)
        {
            return null;
        }

        await using IQuerySession session = store.QuerySession(Tenants.Node);
        IReadOnlyList<Definition> held = await session.Query<Definition>().ToListAsync(cancellationToken);
        return held.FirstOrDefault(d => d.Kind == drafted.Kind && d.Status != DefinitionStatus.Draft && forkBase.IsBaseOf(d));
    }

    private async Task<string> UnresolvedAsync(Project project, long sequence, DefinitionDrafted drafted, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(Tenants.Node);
        IReadOnlyList<Definition> held = await session.Query<Definition>().ToListAsync(cancellationToken);
        string[] versions = [.. held
            .Where(d => d.Kind == drafted.Kind && d.Scope == DefinitionScope.Module && d.Status != DefinitionStatus.Draft && DefinitionResolution.SameName(d.Name, drafted.Name))
            .OrderBy(d => d.VersionNumber)
            .Select(d => d.VersionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        string kind = drafted.Kind.ToString();
        string holds = versions.Length == 0
            ? $"this node holds no module-scope {kind} '{drafted.Name}'"
            : $"this node holds module-scope {kind} '{drafted.Name}' only at {(versions.Length == 1 ? "version" : "versions")} {string.Join(", ", versions)}";
        return $"Project '{project.Name}' stopped exporting before its line {sequence}: {kind} '{drafted.Name}' version {drafted.Version} was forked from {drafted.ForkBase?.Describe()}, a fork base in the earlier shape, and {holds}, so the base cannot be named by its module and content hash.";
    }

    /// <summary>
    /// What a parcel's events are tagged with, read at export: the repos the parcel's tag field holds, from
    /// the field the type version it was created under marks. Parcels and their type versions are read once
    /// per round.
    /// </summary>
    private sealed class ParcelTags(IQuerySession query, DefinitionResolver resolver, string tenant)
    {
        private readonly Dictionary<Guid, Parcel?> parcels = [];
        private readonly Dictionary<string, FieldDeclaration?> tagFields = new(StringComparer.Ordinal);

        public async Task<LineTag> OfAsync(IEvent entry, CancellationToken cancellationToken)
        {
            Parcel? parcel = await ParcelOfAsync(entry.StreamId, cancellationToken);
            FieldDeclaration? field = parcel is null ? null : await TagFieldAsync(parcel.TypeHash, cancellationToken);
            if (parcel is null || field is null)
            {
                return LineTag.ProjectWide;
            }

            IEnumerable<string> names = Names(parcel.ValueOf(field.Name));
            if (entry.Data is ParcelFieldSet set && entry.StreamId == parcel.Id && string.Equals(set.Field, field.Name, StringComparison.Ordinal))
            {
                // The repos the field held before the set, the repos the set moved it to, and the repos it holds now.
                names = names.Concat(Names(set.Value)).Concat(Names(await BeforeAsync(parcel.Id, field.Name, entry.Version, cancellationToken)));
            }

            string[] repos = [.. names.Distinct(StringComparer.Ordinal)];
            return repos.Length == 0
                ? LineTag.ProjectWide
                : LineTag.Repos(repos);
        }

        private static IEnumerable<string> Names(FieldValue? value) =>
            value?.Items?.Where(name => !string.IsNullOrWhiteSpace(name)) ?? [];

        /// <summary>The parcel whose stream this is, or whose flow run's stream it is; null for any other stream.</summary>
        private async Task<Parcel?> ParcelOfAsync(Guid streamId, CancellationToken cancellationToken)
        {
            if (parcels.TryGetValue(streamId, out Parcel? known))
            {
                return known;
            }

            Parcel? parcel = await query.LoadAsync<Parcel>(streamId, cancellationToken);
            if (parcel is null && await query.LoadAsync<FlowRun>(streamId, cancellationToken) is { } flowRun)
            {
                parcel = await ParcelOfAsync(flowRun.ParcelId, cancellationToken);
            }

            parcels[streamId] = parcel;
            return parcel;
        }

        private async Task<FieldDeclaration?> TagFieldAsync(string typeHash, CancellationToken cancellationToken)
        {
            if (!tagFields.TryGetValue(typeHash, out FieldDeclaration? field))
            {
                field = (await resolver.ResolveParcelTypeByHashAsync(tenant, typeHash, cancellationToken))?.TagField;
                tagFields[typeHash] = field;
            }

            return field;
        }

        /// <summary>The value the field held just before the event at <paramref name="version"/> of the parcel's stream.</summary>
        private async Task<FieldValue?> BeforeAsync(Guid parcelId, string field, long version, CancellationToken cancellationToken)
        {
            if (version <= 1)
            {
                return null;
            }

            IReadOnlyList<IEvent> before = await query.Events.FetchStreamAsync(parcelId, version - 1, token: cancellationToken);
            return before.Select(e => e.Data).OfType<ParcelFieldSet>().LastOrDefault(s => string.Equals(s.Field, field, StringComparison.Ordinal))?.Value;
        }
    }
}

/// <summary>
/// What one export of one project did: how many lines it wrote, and every reason a line was not written
/// or a repo not written or pushed, none when the project's ledger is on every remote.
/// </summary>
public sealed record LedgerPublication(string Project, int Written, IReadOnlyList<string> Failures)
{
    public bool Published => Failures.Count == 0;
}
