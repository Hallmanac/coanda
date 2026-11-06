using Coanda.Core;
using Coanda.Core.Identity;
using Coanda.Core.Identity.Events;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Repositories;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.Identity;

/// <summary>
/// The daemon's side of <c>coa join</c> (design section 6): a node that does not hold a project joins it from
/// the project's remote and the fingerprint of the owner's root key, which a person carries from the owner's
/// <c>coa node show</c>. The join fetches the remote's ledger refs into a scratch clone under the node home,
/// finds the owner root record whose key has that fingerprint, and verifies its origin's chain, signed by that
/// key, through both that record and the project's registration. Only then does it write the project's trusted
/// root key record (<see cref="LedgerRoot"/>) with that key, place the clone at the layout path, record
/// the registration's <see cref="ProjectRegistered.RegisteredBy"/> as this node's owner id, and import the
/// project at once, which stores the owner's registration rather than recording one here.
/// </summary>
/// <remarks>
/// A refused join leaves nothing: no registration, no root key record, no clone, no scratch fetch, no owner id
/// record, and no stored event, since each is written only once every check has passed. A join that passed every
/// check and then stopped before the import stored the owner's registration (cancelled, the daemon stopped, or a
/// line of the owner's that does not import) leaves its root key record, its clone, and perhaps its owner id
/// record; the same join run again finds them, verifies the remote again, and picks up from there. A join of a
/// project this node already holds under the same id changes nothing and says so.
/// </remarks>
public sealed class NodeJoiner(
    IDocumentStore store,
    NodeHome home,
    NodeIdentity identity,
    NodeOwner owner,
    ICommandRunner runner,
    LedgerImport import,
    ProjectService projects,
    RepoClones clones,
    ILogger<NodeJoiner> logger)
{
    private readonly Git git = new(runner);

    public async Task<JoinResult> JoinAsync(Principal principal, string remote, string fingerprint, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("node.join", principal);
        if (string.IsNullOrWhiteSpace(remote))
        {
            throw new DomainException("A join needs the project's remote; none was given.");
        }

        if (!KeyFingerprint.IsFingerprint(fingerprint))
        {
            throw new DomainException($"'{fingerprint}' is not a root key fingerprint; a fingerprint is {KeyFingerprint.Rule}, as the owner's coa node show prints it.");
        }

        // The registration gate, not one of the join's own: a project add between the join's checks and the
        // import that stores the owner's registration could register the same name or pin another owner id.
        return await projects.RegisteringAsync(async () =>
        {
            (Project project, string rootKey, bool held) = await PlaceAsync(remote, fingerprint, cancellationToken);
            if (held)
            {
                return new JoinResult(project.Name, identity.NodeId, identity.Key.PublicKey, true, true, [], [], await TrustedAsync(project.Name, rootKey, cancellationToken));
            }

            // The join placed only the clone of the repo it was given; every other repo's clone the import and
            // publication need is made here, as a registration makes them, rather than at the next start.
            await clones.EnsureAsync(project, cancellationToken);
            LedgerImportReport report = await import.ImportJoiningAsync(project, cancellationToken);
            bool registered = await ProjectService.FindAsync(store, project.Name, cancellationToken) is not null;
            return new JoinResult(
                project.Name,
                identity.NodeId,
                identity.Key.PublicKey,
                false,
                registered,
                report.Origins,
                report.Failures,
                await TrustedAsync(project.Name, rootKey, cancellationToken));
        }, cancellationToken);
    }

    /// <summary>
    /// Fetches, decides, and on success places the clone, the root key record, and the owner id, giving the
    /// registration read from the root's chain, the root key, and whether this node already held the project.
    /// </summary>
    private async Task<(Project Project, string RootKey, bool Held)> PlaceAsync(string remote, string fingerprint, CancellationToken cancellationToken)
    {
        // Under the home, so the move into the layout path stays on one volume; its name is no project's.
        string scratch = Path.Combine(home.Path, $".join-{Guid.NewGuid():N}.partial");
        bool placed = false;
        try
        {
            await RepoClones.InitAsync(git, scratch, remote, cancellationToken);
            var peers = new PeerRefs(git, identity.NodeId);
            try
            {
                await peers.FetchAsync(scratch, cancellationToken);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DomainException($"The join was refused: the ledger refs of {remote} could not be fetched: {failure.Message}");
            }

            List<(Guid Origin, List<SignedLine> Lines)> chains = [];
            foreach ((Guid origin, string commit) in await peers.ListAsync(scratch, cancellationToken))
            {
                IReadOnlyList<RefLine> read = await peers.ReadAsync(scratch, commit, null, cancellationToken);
                chains.Add((origin, [.. read.Select(l => SignedLine.Parse(l.Bytes, out _)).TakeWhile(l => l is not null).OfType<SignedLine>()]));
            }

            (Project project, string rootKey) = Decide(remote, fingerprint, chains);
            ProjectRepo repo = project.Repos.First(r => string.Equals(r.Remote, remote, StringComparison.Ordinal));

            IReadOnlyList<Project> registered;
            await using (IQuerySession session = store.QuerySession(Tenants.Node))
            {
                registered = await session.Query<Project>().AnyTenant().ToListAsync(cancellationToken);
            }

            if (registered.FirstOrDefault(p => string.Equals(p.Name, project.Name, StringComparison.OrdinalIgnoreCase)) is { } same)
            {
                return same.Id == project.Id && string.Equals(same.Name, project.Name, StringComparison.Ordinal)
                    ? (same, rootKey, true)
                    : throw new DomainException(
                        $"The join was refused: project '{same.Name}' is already registered on this node under the id {same.Id}, and the remote's project '{project.Name}' is {project.Id}; the node keeps each project in a directory named after it, whatever its letter case.");
            }

            if (owner.RefusalOf(project.RegisteredBy) is { } otherOwner)
            {
                throw new DomainException($"The join was refused: {otherOwner} The registration of project '{project.Name}' names '{project.RegisteredBy}'.");
            }

            if (!RepoLayout.IsPlain(project.Name) || !RepoLayout.IsPlain(repo.Name))
            {
                throw new DomainException(
                    $"The join was refused: project '{project.Name}' or its repo '{repo.Name}' is not a plain directory name, so this node can hold no clone of it.");
            }

            string clone = home.RepoClonePath(project.Name, repo.Name);
            bool resumed = Directory.Exists(clone) || File.Exists(clone);
            if (resumed && !await UnfinishedAsync(project, repo, clone, rootKey, cancellationToken))
            {
                throw new DomainException(
                    $"The join was refused: something is already at {clone}, where the clone of repo '{repo.Name}' goes, and it is not a clone an earlier join of project '{project.Name}' from {remote} left; it is left as it is.");
            }

            // The root key record goes first, so a clone at the layout path for a project not registered here,
            // with a root key record naming this key, is always one an earlier join of it placed and did not
            // finish, which the same join run again picks up (UnfinishedAsync). The move and the owner id record
            // after it are synchronous, so nothing is cancelled between them.
            await using (IDocumentSession session = store.LightweightSession(project.Name))
            {
                session.Store(new LedgerRoot { Key = rootKey });
                await session.SaveChangesAsync(cancellationToken);
            }

            if (!resumed)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(clone) ?? clone);
                    Directory.Move(scratch, clone);
                    placed = true;
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    // No clone was placed, so no root key record may stay for a project this node does not hold.
                    await using IDocumentSession session = store.LightweightSession(project.Name);
                    session.Delete<LedgerRoot>(LedgerRoot.DocumentId);
                    // Uncancellable on purpose: a cancelled cleanup would leave the record it exists to remove.
                    await session.SaveChangesAsync(CancellationToken.None);
                    throw new DomainException($"The join was refused: the fetched clone could not be moved to {clone}: {failure.Message}");
                }
            }

            owner.Record(project.RegisteredBy);
            logger.LogInformation(
                "Joined project '{Project}' ({ProjectId}) from {Remote}, trusting the root key {Key}; its clone is at {Clone}{Resumed}.",
                project.Name, project.Id, remote, rootKey, clone, resumed ? ", placed by an earlier join that did not finish" : string.Empty);
            return (project, rootKey, false);
        }
        finally
        {
            if (!placed)
            {
                Remove(scratch);
            }
        }
    }

    /// <summary>
    /// The registration the root record with the fingerprint's key vouches for, read from that record's origin's
    /// chain, and the key; or the refusal that names why the remote's lines give none.
    /// </summary>
    private (Project Project, string RootKey) Decide(string remote, string fingerprint, List<(Guid Origin, List<SignedLine> Lines)> chains)
    {
        string rootType = TypeNameOf(typeof(OwnerRootRecorded));
        string registrationType = TypeNameOf(typeof(ProjectRegistered));

        // A root record counts only when the key it names signed it, from the node it names.
        List<(Guid Origin, SignedLine Line, OwnerRootRecorded Root)> roots =
        [
            .. from chain in chains
            from line in chain.Lines
            where line.Content.Event.Type == rootType
            let root = Read<OwnerRootRecorded>(line)
            where root is not null
                && IdentityKeys.IsKey(root.Key)
                && KeyFingerprint.Of(root.Key) == fingerprint
                && root.Key == line.Signer
                && root.NodeId == chain.Origin
                && line.Content.Origin == chain.Origin
            select (chain.Origin, line, root),
        ];

        if (roots.Count == 0)
        {
            throw new DomainException(
                $"The join was refused: no owner root record on {remote} names a key with the fingerprint {fingerprint}; check the fingerprint against the owner's coa node show, and that the owner has vouched in the project, which records its root.");
        }

        Guid[] projects = [.. roots.Select(r => r.Line.Content.ProjectId).Distinct().Order()];
        if (projects.Length > 1)
        {
            throw new DomainException(
                $"The join was refused: the lines on {remote} root more than one project at the key with the fingerprint {fingerprint} ({string.Join(", ", projects)}), so which one to join is not known.");
        }

        Guid[] rootNodes = [.. roots.Select(r => r.Origin).Distinct().Order()];
        if (rootNodes.Length > 1)
        {
            throw new DomainException(
                $"The join was refused: more than one node ({string.Join(", ", rootNodes)}) records itself as the root of project {projects[0]} at the key with the fingerprint {fingerprint}; a project has one owner root.");
        }

        (Guid origin, SignedLine rootLine, OwnerRootRecorded rootRecord) = roots[0];
        Guid projectId = rootLine.Content.ProjectId;
        List<SignedLine> lines = chains.First(c => c.Origin == origin).Lines;
        SignedLine? registrationLine = lines.FirstOrDefault(l =>
            l.Content.ProjectId == projectId && l.Content.Event.Type == registrationType && Read<ProjectRegistered>(l)?.ProjectId == projectId);
        if (registrationLine is null || Read<ProjectRegistered>(registrationLine) is not { } registration)
        {
            throw new DomainException(
                $"The join was refused: the chain of node {origin}, whose root record has the fingerprint {fingerprint}, carries no registration of project {projectId}, so the project's name and repos cannot be verified through it.");
        }

        // The prefix is cut at where the two lines sit in the chain, not at the sequences they claim, which nothing
        // has verified yet: a line claiming an earlier sequence than its place would otherwise fall outside it.
        int end = Math.Max(lines.IndexOf(rootLine), lines.IndexOf(registrationLine));
        long through = Math.Max(rootLine.Content.Sequence, registrationLine.Content.Sequence);
        ChainVerification verified = ChainVerifier.Verify(
            [.. lines.Take(end + 1).Select(l => l.Bytes)], projectId, origin, [rootRecord.Key]);
        if (verified.Outcome != ChainOutcome.Verified || verified.VerifiedLines != end + 1 || verified.VerifiedThrough < through)
        {
            string why = verified.Fault is { } fault
                ? $"line {fault.Sequence}: {fault.Reason}"
                : $"it ends at line {verified.VerifiedThrough}, before line {through}";
            throw new DomainException(
                $"The join was refused: the chain of node {origin} does not verify, signed by the root key, through its root record (line {rootLine.Content.Sequence}) and the project's registration (line {registrationLine.Content.Sequence}): {why}.");
        }

        if (!registration.ReposRecorded.Any(r => string.Equals(r.Remote, remote, StringComparison.Ordinal)))
        {
            throw new DomainException(
                $"The join was refused: {remote} is not exactly the remote of any repo of project '{registration.Name}', whose repos are {string.Join(", ", registration.ReposRecorded.Select(r => $"{r.Name} ({r.Remote})"))}; join with the remote exactly as its registration records it, which the owner's coa project show prints.");
        }

        return (Project.Create(registration), rootRecord.Key);
    }

    /// <summary>
    /// Whether what is at <paramref name="clone"/> is the clone an earlier join of <paramref name="project"/> from
    /// the repo's remote placed and did not finish: the project is not registered here (the caller checked), its
    /// root key record names <paramref name="rootKey"/>, which only a join writes for a project not registered
    /// here, and the clone is a bare repository whose origin is the repo's remote.
    /// </summary>
    private async Task<bool> UnfinishedAsync(Project project, ProjectRepo repo, string clone, string rootKey, CancellationToken cancellationToken)
    {
        LedgerRoot? root;
        await using (IQuerySession session = store.QuerySession(project.Name))
        {
            root = await session.LoadAsync<LedgerRoot>(LedgerRoot.DocumentId, cancellationToken);
        }

        if (root?.Key != rootKey || !Directory.Exists(clone))
        {
            return false;
        }

        CommandResult bare = await git.TryRunAsync(Git.Against(clone, "rev-parse", "--is-bare-repository"), cancellationToken);
        CommandResult origin = await git.TryRunAsync(Git.Against(clone, "config", "--get-all", $"remote.{Git.Remote}.url"), cancellationToken);
        return bare is { Succeeded: true, Truncated: false }
            && bare.StandardOutput.Trim() == "true"
            && origin is { Succeeded: true, Truncated: false }
            && origin.StandardOutput.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries) is [string url]
            && string.Equals(url, repo.Remote, StringComparison.Ordinal);
    }

    /// <summary>Whether this node's own key may sign for it in the project by the identity records this node now holds on lines.</summary>
    private async Task<bool> TrustedAsync(string tenant, string rootKey, CancellationToken cancellationToken)
    {
        IReadOnlyList<IdentityRecord> records = await IdentityRecords.ReadAsync(store, tenant, null, cancellationToken);
        return IdentityTrust.Evaluate(rootKey, records).MaySign(identity.Key.PublicKey, identity.NodeId).MaySign;
    }

    private string TypeNameOf(Type type) => store.Options.EventGraph.EventMappingFor(type).EventTypeName;

    /// <summary>The line's event as <typeparamref name="T"/>, or null when its data, which nothing has trusted yet, does not read as one.</summary>
    private T? Read<T>(SignedLine line)
        where T : class
    {
        try
        {
            return store.Options.Serializer.FromJson(typeof(T), line.Content.Event.Data) as T;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Deletes the scratch fetch, clearing the read-only mark git leaves on its objects on Windows first.</summary>
    private void Remove(string scratch)
    {
        if (!Directory.Exists(scratch))
        {
            return;
        }

        try
        {
            foreach (FileInfo file in new DirectoryInfo(scratch).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }

            Directory.Delete(scratch, recursive: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            logger.LogError(failure, "The join's scratch fetch at {Scratch} could not be removed; remove it by hand.", scratch);
        }
    }
}

/// <summary>
/// What a join came to: the project's name, this node's id and public key, whether this node already held the
/// project (and so changed nothing), whether the project is now registered here (an import that stopped before the
/// owner's registration leaves it unregistered, and the same join run again picks up from there), the import's result for each origin and every reason a clone could not be
/// fetched or read, and whether this node's own key is yet trusted in the project.
/// </summary>
public sealed record JoinResult(
    string Project,
    Guid NodeId,
    string PublicKey,
    bool AlreadyHeld,
    bool Registered,
    IReadOnlyList<OriginImport> Origins,
    IReadOnlyList<string> Failures,
    bool Trusted);
