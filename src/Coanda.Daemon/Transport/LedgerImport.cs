using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Identity;
using Coanda.Core.Identity.Events;
using Coanda.Core.Projects;
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
/// The node's import of what its peers authored (design section 6): for each project, every clone fetches the
/// remote's ledger refs into the namespace set aside for peers' refs, and each fetched ref named for another
/// node is read as that node's chain in the project, from the line at the sequence this node holds for it, or
/// from its first line when it holds none. A line is stored only when the chain verifies up to it from the
/// held position, its signer key may sign for its origin by <see cref="IdentityTrust"/>, and its event is in
/// this build's current shape. Each stored event is appended to the stream the line names in the project's
/// tenant, under an event id this node's store gives it, and in the same save the node keeps the line as a
/// <see cref="LedgerLine"/> keyed by that id, so no event is stored without its record or a record without its
/// event, and a reader finds a stored event's origin through its record.
/// </summary>
/// <remarks>
/// <para>
/// The first line that fails a check stops its origin's import at the line before it, for the round: the
/// fault and the line's sequence are reported, the origin's other refs and the project's other origins go on,
/// and the next round tries the origin again from the position held. A line the store refuses stops its origin
/// the same way. A fetch that fails is logged and tried again the next round, and the clone's refs as last
/// fetched are still read.
/// </para>
/// <para>
/// The trust function weighs the identity events in every line this node holds a record of: other origins'
/// lines stored before or in the same round, and this node's own lines once its export has written them. An
/// origin's verified lines are admitted together with the identity records of the admitted lines themselves,
/// which is what admits the root's chain before its root record, and are saved only in runs the stored
/// records already admit, so no line is stored on a record that is not. The root key it trusts is the
/// project's <see cref="LedgerRoot"/>, never a key a line carries; a project with none imports nothing from
/// another origin. Because a vouch can arrive in one origin's lines and admit another's, a round reads again
/// every origin that stopped while any origin stored a line, so the order the refs are read in does not decide
/// what the round stores.
/// </para>
/// <para>
/// Each project's import runs under one turn, so the loop and <see cref="ImportAsync(string, CancellationToken)"/>
/// never import the same project at once, and a line already held is never stored again.
/// </para>
/// </remarks>
public sealed class LedgerImport(
    IDocumentStore store,
    NodeHome home,
    NodeIdentity identity,
    ICommandRunner runner,
    TimeProvider clock,
    ILogger<LedgerImport> logger) : IHostedService, IDisposable
{
    /// <summary>
    /// How often the daemon fetches and imports when <c>coa daemon run</c> starts it: a line a peer pushes is
    /// stored within this long. Fifteen seconds matches the export's interval, so a line reaches a peer within
    /// two intervals of being recorded, and a round with nothing new costs one fetch per clone; a command that
    /// must see its peers before it answers calls <see cref="ImportAsync(string, CancellationToken)"/> instead.
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    /// <summary>The most lines one save stores.</summary>
    private const int BatchSize = 256;

    private readonly SemaphoreSlim turn = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly PeerRefs peers = new(new Git(runner), identity.NodeId);
    private readonly Lazy<IReadOnlyDictionary<string, Type>> eventTypes = new(() => EventTypesOf(store));

    /// <summary>
    /// For each clone and origin, the commit of the origin's ref last read to its end with no fault, so a round
    /// whose ref is unchanged reads nothing of it. It is kept for the daemon's run alone.
    /// </summary>
    private readonly Dictionary<(string Clone, Guid Origin), string> readThrough = [];

    private Task running = Task.CompletedTask;

    /// <summary>Fetches and imports every project once, then again every <paramref name="interval"/>, on the daemon's own worker.</summary>
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
            logger.LogWarning("The daemon stopped before the ledger import finished its round.");
        }
    }

    public void Dispose()
    {
        stopping.Dispose();
        turn.Dispose();
    }

    /// <summary>Fetches and imports every project, each apart: one project's failure is logged and the others still import.</summary>
    public async Task<IReadOnlyList<LedgerImportReport>> ImportAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Project> projects;
        await using (IQuerySession session = store.QuerySession(Tenants.Node))
        {
            projects = await session.Query<Project>().AnyTenant().ToListAsync(cancellationToken);
        }

        List<LedgerImportReport> reports = [];
        foreach (Project project in projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            reports.Add(await TurnAsync(project, cancellationToken));
        }

        return reports;
    }

    /// <summary>
    /// Fetches and imports <paramref name="project"/> now, for a command that must see its peers before it
    /// answers: for each origin it read, how many lines it stored, the sequence it now holds, and the fault that
    /// stopped it, if any.
    /// </summary>
    public async Task<LedgerImportReport> ImportAsync(string project, CancellationToken cancellationToken)
    {
        string tenant = Tenants.RequireProject(project);
        Project found = await ProjectService.FindAsync(store, tenant, cancellationToken)
            ?? throw new DomainException($"No project '{tenant}' is registered on this node.");
        return await TurnAsync(found, cancellationToken);
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ImportAsync(cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure)
            {
                logger.LogError(failure, "The ledger import could not read the projects; it tries again in {Interval}.", interval);
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

    private async Task<LedgerImportReport> TurnAsync(Project project, CancellationToken cancellationToken)
    {
        await turn.WaitAsync(cancellationToken);
        try
        {
            return await ImportProjectAsync(project, cancellationToken);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(failure, "The ledger import of project {Project} failed; the next round tries again.", project.Name);
            return new LedgerImportReport(project.Name, [], [failure.Message]);
        }
        finally
        {
            turn.Release();
        }
    }

    private async Task<LedgerImportReport> ImportProjectAsync(Project project, CancellationToken cancellationToken)
    {
        List<string> failures = [];
        Dictionary<Guid, List<Source>> origins = [];
        foreach (ProjectRepo repo in project.Repos)
        {
            string? clone = RepoLayout.IsPlain(project.Name) && RepoLayout.IsPlain(repo.Name)
                ? home.RepoClonePath(project.Name, repo.Name)
                : null;
            if (clone is null || !Directory.Exists(clone))
            {
                string where = clone is null
                    ? "under the node home, since its name or the project's is not a plain directory name"
                    : $"at {clone}; the next start makes it";
                failures.Add($"Repo '{repo.Name}' of project '{project.Name}' has no clone {where}, so no peer's ledger is read from it.");
                continue;
            }

            try
            {
                await peers.FetchAsync(clone, cancellationToken);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                string reason = $"The peers' ledgers of project '{project.Name}' were not fetched into repo '{repo.Name}': {failure.Message}";
                logger.LogWarning("{Reason} The next round tries again.", reason);
                failures.Add(reason);
            }

            try
            {
                foreach ((Guid origin, string commit) in await peers.ListAsync(clone, cancellationToken))
                {
                    if (!origins.TryGetValue(origin, out List<Source>? sources))
                    {
                        sources = [];
                        origins[origin] = sources;
                    }

                    sources.Add(new Source(repo.Name, clone, commit));
                }
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                string reason = $"The peers' ledgers of project '{project.Name}' could not be listed in repo '{repo.Name}': {failure.Message}";
                logger.LogWarning("{Reason} The next round tries again.", reason);
                failures.Add(reason);
            }
        }

        Guid[] ordered = [.. origins.Keys.Order()];
        Dictionary<Guid, (int Stored, ImportFault? Fault)> results = ordered.ToDictionary(o => o, _ => (0, (ImportFault?)null));
        if (ordered.Length > 0)
        {
            string? root = await RootAsync(project.Name, cancellationToken);
            if (root is null)
            {
                string reason = $"Project '{project.Name}' has no root key record on this node, so no line from another origin can be trusted and none is imported.";
                foreach (Guid origin in ordered)
                {
                    results[origin] = (0, new ImportFault(null, ImportFaultKind.NoTrustedRoot, reason));
                }
            }
            else
            {
                // A vouch stored from one origin can admit another that stopped earlier in the round, so the
                // stopped ones are read again while any origin stores a line.
                IEnumerable<Guid> pending = ordered;
                bool stored = true;
                while (stored)
                {
                    stored = false;
                    foreach (Guid origin in pending.ToArray())
                    {
                        (int count, ImportFault? fault) = await ImportOriginAsync(project, root, origin, origins[origin], cancellationToken);
                        results[origin] = (results[origin].Stored + count, fault);
                        stored |= count > 0;
                    }

                    pending = ordered.Where(o => results[o].Fault is not null);
                }
            }
        }

        List<OriginImport> imported = [];
        foreach (Guid origin in ordered)
        {
            (int count, ImportFault? fault) = results[origin];
            long? held = (await HeldAsync(project.Name, origin, cancellationToken))?.Sequence;
            imported.Add(new OriginImport(origin, count, held, fault));
            if (fault is not null)
            {
                string stoppedAt = fault.Sequence is { } at
                    ? $"line {at}"
                    : "its first line read";
                string holds = held is { } h
                    ? $"line {h}"
                    : "none of its lines";
                logger.LogWarning(
                    "The import of origin {Origin} in project {Project} stopped at {Line}: {Reason} It stored {Stored} lines, holds {Held}, and the next round tries again from there.",
                    origin, project.Name, stoppedAt, fault.Reason, count, holds);
            }
            else if (count > 0)
            {
                logger.LogInformation("Imported {Stored} lines of origin {Origin} in project {Project}, through line {Held}.", count, origin, project.Name, held);
            }
        }

        return new LedgerImportReport(project.Name, imported, failures);
    }

    /// <summary>
    /// Imports one origin from each ref of it the project's clones hold. A ref that ends before the sequence
    /// held is reported only when no other ref of the origin was read without a fault.
    /// </summary>
    private async Task<(int Stored, ImportFault? Fault)> ImportOriginAsync(Project project, string root, Guid origin, List<Source> sources, CancellationToken cancellationToken)
    {
        int stored = 0;
        ImportFault? fault = null;
        ImportFault? rewind = null;
        bool clean = false;
        foreach (Source source in sources)
        {
            if (readThrough.TryGetValue((source.ClonePath, origin), out string? read) && read == source.Commit)
            {
                clean = true;
                continue;
            }

            (int count, ImportFault? found) = await ImportRefAsync(project, root, origin, source, cancellationToken);
            stored += count;
            switch (found)
            {
                case null:
                    readThrough[(source.ClonePath, origin)] = source.Commit;
                    clean = true;
                    break;
                case { Kind: ImportFaultKind.Rewind }:
                    readThrough.Remove((source.ClonePath, origin));
                    rewind ??= found;
                    break;
                default:
                    readThrough.Remove((source.ClonePath, origin));
                    fault ??= found;
                    break;
            }
        }

        return clean
            ? (stored, fault)
            : (stored, fault ?? rewind);
    }

    /// <summary>Reads one ref of <paramref name="origin"/> from the position held and stores each line that passes every check, up to the first that does not.</summary>
    private async Task<(int Stored, ImportFault? Fault)> ImportRefAsync(Project project, string root, Guid origin, Source source, CancellationToken cancellationToken)
    {
        string tenant = project.Name;
        LedgerLine? held = await HeldAsync(tenant, origin, cancellationToken);
        HeldPosition? position = held is null
            ? null
            : new HeldPosition(held.Sequence, held.Hash);
        List<(RefLine Line, SignedLine? Parsed)> run;
        try
        {
            run = Parsed(await peers.ReadAsync(source.ClonePath, source.Commit, held?.Segment, cancellationToken));
            if (held is not null && held.Segment.Length > 0 && Held(run, held) == run.Count)
            {
                // Nothing from the held line's segment on: the ref is read whole before it is called rewound.
                run = Parsed(await peers.ReadAsync(source.ClonePath, source.Commit, null, cancellationToken));
            }
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            return (0, new ImportFault(null, ImportFaultKind.Unreadable, $"Its ref in repo '{source.Repo}' could not be read: {failure.Message}"));
        }

        // From the line at the held sequence on: the lines before it that parse are what this node holds already.
        if (held is not null)
        {
            int skipped = Held(run, held);

            if (skipped == run.Count)
            {
                long? last = run.Count == 0
                    ? null
                    : run[^1].Parsed?.Content.Sequence;
                string ends = (last, run.Count) switch
                {
                    ({ } at, _) => $"ends at line {at}",
                    (_, 0) => "holds no line",
                    _ => "ends in a line that does not parse",
                };
                return (0, new ImportFault(last, ImportFaultKind.Rewind, $"Its ref in repo '{source.Repo}' {ends}, before line {held.Sequence}, the last this node holds of it: the origin's chain has been rewound."));
            }

            run.RemoveRange(0, skipped);
        }

        string[] signers = [.. run.Select(l => l.Parsed?.Signer).OfType<string>().Distinct(StringComparer.Ordinal)];
        ChainVerification verification = ChainVerifier.Verify([.. run.Select(l => l.Line.Bytes)], project.Id, origin, signers, position);

        // The verified lines after the one held, in order, up to the first whose event is not in this build's current shape.
        List<Pending> candidates = [];
        ImportFault? fault = null;
        for (int index = 0; index < verification.VerifiedLines; index++)
        {
            (RefLine refLine, SignedLine? parsed) = run[index];
            SignedLine line = parsed ?? throw new InvalidOperationException("A verified line parses.");
            long sequence = line.Content.Sequence;
            if (held is not null && sequence <= held.Sequence)
            {
                continue;
            }

            (object? current, ImportFault? shape) = CurrentOf(line);
            if (current is not { } data)
            {
                fault = shape;
                break;
            }

            IdentityRecord? record = data is IIdentityEvent identityEvent
                ? new IdentityRecord(identityEvent, line.Signer, origin, sequence)
                : null;
            candidates.Add(new Pending(line, refLine.Segment, data, record));
        }

        IReadOnlyList<IdentityRecord> records = await RecordsAsync(tenant, cancellationToken);
        (List<Pending> admitted, ImportFault? untrusted) = Admit(root, origin, records, candidates);
        (int stored, ImportFault? refused) = await StoreAsync(tenant, admitted, Boundaries(root, origin, records, admitted), cancellationToken);
        fault = refused ?? untrusted ?? fault;
        fault ??= (verification.Outcome, verification.Fault) switch
        {
            (_, { } broken) => new ImportFault(broken.Sequence, KindOf(broken.Kind), broken.Reason),
            (ChainOutcome.Rewind, _) => new ImportFault(verification.VerifiedThrough, ImportFaultKind.Rewind, $"Its ref in repo '{source.Repo}' ends at line {verification.VerifiedThrough}, before line {held?.Sequence}, the last this node holds of it: the origin's chain has been rewound."),
            _ => null,
        };
        return (stored, fault);
    }

    private static List<(RefLine Line, SignedLine? Parsed)> Parsed(IReadOnlyList<RefLine> lines) =>
        [.. lines.Select(l => (l, SignedLine.Parse(l.Bytes, out _)))];

    /// <summary>How many lines at the start of <paramref name="run"/> parse and come before the line held.</summary>
    private static int Held(List<(RefLine Line, SignedLine? Parsed)> run, LedgerLine held) =>
        run.TakeWhile(l => l.Parsed is { } parsed && parsed.Content.Sequence < held.Sequence).Count();

    /// <summary>
    /// The longest run of <paramref name="candidates"/> from the first whose every line's signer may sign for
    /// <paramref name="origin"/> by the records this node holds together with the identity records of that run's
    /// own lines, and the fault of the first line left out, if any. A run's own records are what admit the
    /// root's chain, whose lines before its root record are signed by the key that record names; the trust
    /// function counts a record only when its signer may sign for its origin, so no run admits itself but by a
    /// root record of the trusted key or by records that key's trust reaches. A record can withdraw trust as
    /// well as grant it (a conflicting vouch, a revocation, a member's removal), so a run is never judged by
    /// the records of lines after it: an identity event in a line that is not stored never counts.
    /// </summary>
    /// <remarks>
    /// A run's own records change only at a line that carries one, so the runs holding the first
    /// <c>k</c> of the candidates' records share one verdict. From the most records down, the first <c>k</c>
    /// whose verdict admits every line through the <c>k</c>-th record's line gives the longest run: it reaches
    /// as far as that verdict admits lines before the next record's line, and every longer run holds more
    /// records, which a larger <c>k</c> has already refused.
    /// </remarks>
    private static (List<Pending> Admitted, ImportFault? Fault) Admit(string root, Guid origin, IReadOnlyList<IdentityRecord> held, List<Pending> candidates)
    {
        int[] recordLines = [.. Enumerable.Range(0, candidates.Count).Where(i => candidates[i].Record is not null)];
        (int Line, SigningAnswer Answer)? refusedWithMore = null;
        for (int k = recordLines.Length; k >= 0; k--)
        {
            Func<string, SigningAnswer> answerFor = AnswersOf(
                IdentityTrust.Evaluate(root, [.. held, .. recordLines.Take(k).Select(i => candidates[i].Record).OfType<IdentityRecord>()]),
                origin);

            // A run holding exactly these k records holds every line through the k-th record's line, and stops before the next's.
            int through = k == 0
                ? 0
                : recordLines[k - 1] + 1;
            int next = k == recordLines.Length
                ? candidates.Count
                : recordLines[k];
            int refused = candidates.FindIndex(0, through, p => !answerFor(p.Line.Signer).MaySign);
            if (refused >= 0)
            {
                refusedWithMore = (refused, answerFor(candidates[refused].Line.Signer));
                continue;
            }

            int end = through;
            while (end < next && answerFor(candidates[end].Line.Signer).MaySign)
            {
                end++;
            }

            if (end == candidates.Count)
            {
                return (candidates, null);
            }

            SignedLine left = candidates[end].Line;
            (int Line, SigningAnswer Answer) cause = end < next || refusedWithMore is not { } withMore
                ? (end, answerFor(left.Signer))
                : withMore;
            SignedLine refusedLine = candidates[cause.Line].Line;
            string reason = cause.Line == end
                ? $"Line {left.Content.Sequence} is signed by {left.Signer}, which may not sign for origin {origin}: {Describe(cause.Answer.Refusal)}."
                : $"Line {left.Content.Sequence}'s identity event would leave line {refusedLine.Content.Sequence}, signed by {refusedLine.Signer}, unable to sign for origin {origin}: {Describe(cause.Answer.Refusal)}.";
            return (candidates.GetRange(0, end), new ImportFault(left.Content.Sequence, ImportFaultKind.UntrustedSigner, reason));
        }

        throw new UnreachableException("A run of no records is always judged, and admits at least the empty run.");
    }

    /// <summary>Whether each signer may sign for <paramref name="origin"/> by <paramref name="verdict"/>, asked once per signer.</summary>
    private static Func<string, SigningAnswer> AnswersOf(IdentityVerdict verdict, Guid origin)
    {
        var answers = new Dictionary<string, SigningAnswer>(StringComparer.Ordinal);
        return signer => answers.TryGetValue(signer, out SigningAnswer? known)
            ? known
            : answers[signer] = verdict.MaySign(signer, origin);
    }

    /// <summary>
    /// For each admitted line, whether the lines through it are admitted by the records this node holds and
    /// theirs alone, so that a save may end after it and no line is stored on a record that is not. The last is
    /// always so, by <see cref="Admit"/>.
    /// </summary>
    private static bool[] Boundaries(string root, Guid origin, IReadOnlyList<IdentityRecord> held, List<Pending> admitted)
    {
        bool[] safe = new bool[admitted.Count];
        List<IdentityRecord> records = [.. held];
        IdentityVerdict verdict = IdentityTrust.Evaluate(root, records);
        HashSet<string> signers = new(StringComparer.Ordinal);
        for (int index = 0; index < admitted.Count; index++)
        {
            signers.Add(admitted[index].Line.Signer);
            if (admitted[index].Record is { } record)
            {
                records.Add(record);
                verdict = IdentityTrust.Evaluate(root, records);
            }

            safe[index] = signers.All(signer => verdict.MaySign(signer, origin).MaySign);
        }

        return safe;
    }

    /// <summary>
    /// Stores the admitted lines in order, a save at most every <see cref="BatchSize"/> lines and before a line
    /// whose stream an earlier line of the save already named after another's, so the store's order of their
    /// events is the chain's; a save ends only after a line <paramref name="safe"/> marks.
    /// </summary>
    private async Task<(int Stored, ImportFault? Fault)> StoreAsync(string tenant, List<Pending> admitted, bool[] safe, CancellationToken cancellationToken)
    {
        int stored = 0;
        int from = 0;
        HashSet<Guid> streams = [];
        for (int index = 0; index <= admitted.Count; index++)
        {
            bool last = index == admitted.Count;
            Guid? stream = last
                ? null
                : admitted[index].Line.Content.StreamId;
            bool interleaves = stream is { } next
                && index > from
                && admitted[index - 1].Line.Content.StreamId != next
                && streams.Contains(next);
            if (index > from && (last || (safe[index - 1] && (index - from >= BatchSize || interleaves))))
            {
                (int saved, ImportFault? refused) = await SaveAsync(tenant, admitted, safe, from, index, cancellationToken);
                stored += saved;
                if (refused is not null)
                {
                    return (stored, refused);
                }

                from = index;
                streams.Clear();
            }

            if (stream is { } added)
            {
                streams.Add(added);
            }
        }

        return (stored, null);
    }

    /// <summary>
    /// Stores the lines from <paramref name="from"/> up to <paramref name="to"/> in one save; when the store
    /// refuses it, stores them again one save to each run ending after a line <paramref name="safe"/> marks, up
    /// to the run it refuses, which stops the origin at that run's first line with the store's reason.
    /// </summary>
    private async Task<(int Saved, ImportFault? Fault)> SaveAsync(string tenant, List<Pending> lines, bool[] safe, int from, int to, CancellationToken cancellationToken)
    {
        try
        {
            await SaveAllAsync(tenant, lines.GetRange(from, to - from), cancellationToken);
            return (to - from, null);
        }
        catch (Exception refused) when (!cancellationToken.IsCancellationRequested)
        {
            int runs = Enumerable.Range(from, to - from).Count(i => safe[i] || i == to - 1);
            if (runs == 1)
            {
                return (0, Refused(lines[from], refused));
            }
        }

        int start = from;
        for (int index = from; index < to; index++)
        {
            if (!safe[index] && index < to - 1)
            {
                continue;
            }

            try
            {
                await SaveAllAsync(tenant, lines.GetRange(start, index + 1 - start), cancellationToken);
            }
            catch (Exception refused) when (!cancellationToken.IsCancellationRequested)
            {
                return (start - from, Refused(lines[start], refused));
            }

            start = index + 1;
        }

        return (to - from, null);
    }

    private static ImportFault Refused(Pending pending, Exception refused) =>
        new(pending.Line.Content.Sequence, ImportFaultKind.StoreRefused, $"Line {pending.Line.Content.Sequence} passed every check, and the store refused to append it: {refused.Message}");

    /// <summary>Appends each line's event to the stream it names and keeps its record, all in one save.</summary>
    private async Task SaveAllAsync(string tenant, IReadOnlyList<Pending> batch, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession(tenant);
        foreach (Pending pending in batch)
        {
            LineContent content = pending.Line.Content;
            StreamAction action = session.Events.Append(content.StreamId, pending.Data);
            IEvent appended = action.Events[^1];
            appended.Timestamp = content.RecordedAt;
            session.Store(new LedgerLine
            {
                Id = appended.Id,
                Origin = content.Origin,
                Sequence = content.Sequence,
                PreviousHash = content.PreviousHash,
                Tag = content.Tag.RepoNames,
                Signature = pending.Line.Signature,
                Hash = pending.Line.Hash,
                Segment = pending.Segment,
                Line = Encoding.UTF8.GetString(pending.Line.Bytes),
            });
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The line's event as this build's type of it, or the fault that refuses it: a type this build has no event
    /// type for, an earlier shape, data that does not read as the type, or data that holds what this build's
    /// type of it does not, as a later build's shape would. The event is stored as this build writes it, so a
    /// line is stored only when that is the line's data exactly, member for member, and the stored event and
    /// the signed line kept beside it never differ.
    /// </summary>
    private (object? Data, ImportFault? Fault) CurrentOf(SignedLine line)
    {
        StoredEvent stored = line.Content.Event;
        long sequence = line.Content.Sequence;
        if (!eventTypes.Value.TryGetValue(stored.Type, out Type? type))
        {
            return (null, new ImportFault(sequence, ImportFaultKind.EarlierShape, $"Line {sequence} is a '{stored.Type}', a type this build has no event type for: an earlier shape, which import refuses."));
        }

        if (EarlierRecords.EarlierShapeOf(stored) is { } why)
        {
            return (null, new ImportFault(sequence, ImportFaultKind.EarlierShape, $"Line {sequence} is in an earlier shape, which import refuses: {why}."));
        }

        object? data;
        try
        {
            data = store.Options.Serializer.FromJson(type, stored.Data);
        }
        catch (Exception unread) when (unread is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            data = null;
        }

        if (data is null)
        {
            return (null, new ImportFault(sequence, ImportFaultKind.EarlierShape, $"Line {sequence}'s data does not read as this build's '{stored.Type}'."));
        }

        return JsonNode.DeepEquals(JsonNode.Parse(stored.Data), JsonNode.Parse(store.Options.Serializer.ToJson(data)))
            ? (data, null)
            : (null, new ImportFault(sequence, ImportFaultKind.LaterShape, $"Line {sequence}'s data is not this build's '{stored.Type}' as it writes one: it holds a member this build does not know or a value it writes otherwise, as a later build's shape would, and storing it would drop that from the stored event."));
    }

    /// <summary>The identity events in every line this node holds a record of, each with its signer key, origin, and sequence.</summary>
    private async Task<IReadOnlyList<IdentityRecord>> RecordsAsync(string tenant, CancellationToken cancellationToken)
    {
        string[] names = [.. IdentityTypeNames(eventTypes.Value)];
        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<IEvent> events = await session.Events.QueryEventsAsync(e => names.Contains(e.EventTypeName), cancellationToken);
        Guid[] ids = [.. events.Select(e => e.Id)];
        IReadOnlyList<LedgerLine> lines = ids.Length == 0
            ? []
            : await session.Query<LedgerLine>().Where(l => ids.Contains(l.Id)).ToListAsync(cancellationToken);
        Dictionary<Guid, LedgerLine> byEvent = lines.ToDictionary(l => l.Id);
        List<IdentityRecord> records = [];
        foreach (IEvent entry in events)
        {
            if (entry.Data is IIdentityEvent identityEvent
                && byEvent.TryGetValue(entry.Id, out LedgerLine? record)
                && SignedLine.Parse(Encoding.UTF8.GetBytes(record.Line), out _) is { } line)
            {
                records.Add(new IdentityRecord(identityEvent, line.Signer, record.Origin, record.Sequence));
            }
        }

        return records;
    }

    /// <summary>The project's trusted root key, or null when it has no root key record or the record's key is not a key.</summary>
    private async Task<string?> RootAsync(string tenant, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(tenant);
        LedgerRoot? root = await session.LoadAsync<LedgerRoot>(LedgerRoot.DocumentId, cancellationToken);
        return root is not null && IdentityKeys.IsKey(root.Key)
            ? root.Key
            : null;
    }

    /// <summary>The last line this node holds of <paramref name="origin"/> in the project, or null when it holds none.</summary>
    private async Task<LedgerLine?> HeldAsync(string tenant, Guid origin, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(tenant);
        return await session.Query<LedgerLine>()
            .Where(l => l.Origin == origin)
            .OrderByDescending(l => l.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string Describe(SigningRefusal? refusal) =>
        refusal switch
        {
            SigningRefusal.NoRoot => "the identity records this node holds have no root record signed by the root key it trusts for the project",
            SigningRefusal.Revoked => "the key is revoked",
            SigningRefusal.MemberRemoved => "the key is the root key of a removed member",
            SigningRefusal.Conflicted => "the vouches this node holds pair the origin or the key with another",
            SigningRefusal.VouchersMayNotSign => "the only vouches for it are signed by keys that may not sign",
            SigningRefusal.NoCountingVouch => "no counting vouch this node holds gives the origin that key",
            _ => "the trust function refused it",
        };

    private static ImportFaultKind KindOf(ChainFaultKind kind) =>
        kind switch
        {
            ChainFaultKind.Unparsable => ImportFaultKind.Unparsable,
            ChainFaultKind.BadSignature => ImportFaultKind.BadSignature,
            ChainFaultKind.UnacceptedSigner => ImportFaultKind.UntrustedSigner,
            ChainFaultKind.WrongOrigin => ImportFaultKind.WrongOrigin,
            ChainFaultKind.WrongProject => ImportFaultKind.WrongProject,
            ChainFaultKind.WrongSequence => ImportFaultKind.WrongSequence,
            ChainFaultKind.WrongPreviousHash => ImportFaultKind.WrongPreviousHash,
            ChainFaultKind.HeldHashMismatch => ImportFaultKind.HeldHashMismatch,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A chain fault this import does not know."),
        };

    /// <summary>Every event type this build has, by the type name the store records it under: each type of the core's <c>Events</c> namespaces.</summary>
    private static IReadOnlyDictionary<string, Type> EventTypesOf(IDocumentStore store) =>
        typeof(Ids).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsNested: false, IsGenericTypeDefinition: false }
                && t.Namespace?.EndsWith(".Events", StringComparison.Ordinal) == true
                && t.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
            .ToDictionary(t => store.Options.EventGraph.EventMappingFor(t).EventTypeName, StringComparer.Ordinal);

    private static IEnumerable<string> IdentityTypeNames(IReadOnlyDictionary<string, Type> types) =>
        types.Where(t => t.Value.IsAssignableTo(typeof(IIdentityEvent))).Select(t => t.Key);

    /// <summary>One ref of an origin: the repo and clone that hold it, and the commit it names.</summary>
    private sealed record Source(string Repo, string ClonePath, string Commit);

    /// <summary>A line that passed every check, the segment it was read from, its event, and the event's identity record when it is an identity event.</summary>
    private sealed record Pending(SignedLine Line, string Segment, object Data, IdentityRecord? Record);
}
