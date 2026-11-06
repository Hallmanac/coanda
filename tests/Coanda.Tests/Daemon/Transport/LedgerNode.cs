using System.Text;
using Coanda.Core;
using Coanda.Core.Identity;
using Coanda.Core.Identity.Events;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Identity;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Transport;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Repositories;
using Coanda.Tests.Daemon.Commands;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>
/// A node home for the export and import tests: a store holding the software module's definitions, the
/// node's identity, the export, the import, and for each project a local bare repository standing in for
/// its one repo's remote, under the home, the only remote these tests push to and fetch from. The clones
/// are the core's, made by <see cref="RepoClones"/> with real git, which contacts nothing to make one. The
/// export's own git commands are recorded in <see cref="ExportGit"/>, and the import's in <see cref="ImportGit"/>.
/// </summary>
internal sealed class LedgerNode : IAsyncDisposable
{
    public static readonly CancellationToken None = CancellationToken.None;

    private static readonly ProcessCommandRunner Runner = new();

    private readonly TemporaryHome temp;

    private bool storeClosed;

    private LedgerNode(TemporaryHome temp, IDocumentStore store, NodeIdentity identity)
    {
        this.temp = temp;
        Store = store;
        Identity = identity;
        Export = ExportAs(identity);
        var loggers = new LoggerFactory([Logs]);
        LostClaims = new LostClaims(store, new FlowRunService(store, TimeProvider.System), new Logger<LostClaims>(loggers));
        Import = new LedgerImport(store, temp.Home, identity, ImportGit, TimeProvider.System, new Logger<LedgerImport>(loggers), LostClaims);
        ImportWithoutYield = new LedgerImport(store, temp.Home, identity, ImportGit, TimeProvider.System, new Logger<LedgerImport>(loggers));
        Claimant = new ParcelClaimant(store, new ParcelService(store, TimeProvider.System), Export, Import, identity);
        Owner = NodeOwner.Of(temp.Home);
        Voucher = new NodeVoucher(store, Export, identity, TimeProvider.System);
    }

    /// <summary>This node's owner, read from its home when the node started.</summary>
    public NodeOwner Owner { get; }

    /// <summary>The vouch command's side of this node, publishing through <see cref="Export"/>.</summary>
    public NodeVoucher Voucher { get; }

    /// <summary>The join command's side of this node, as <see cref="Owner"/>, importing through <see cref="Import"/>.</summary>
    public NodeJoiner Joiner => JoinerOf(Owner);

    /// <summary>The join command's side of this node as <paramref name="owner"/>, its fetch's git commands recorded in <see cref="ImportGit"/>.</summary>
    public NodeJoiner JoinerOf(NodeOwner owner) =>
        new(
            Store,
            Home,
            Identity,
            owner,
            ImportGit,
            Import,
            new ProjectService(Store, TimeProvider.System),
            new RepoClones(Store, Home, Runner, new Logger<RepoClones>(new LoggerFactory([Logs]))),
            new Logger<NodeJoiner>(new LoggerFactory([Logs])));

    /// <summary>The fingerprint of this node's key, which <c>coa node show</c> prints.</summary>
    public string Fingerprint => KeyFingerprint.Of(Identity.Key.PublicKey);

    public NodeHome Home => temp.Home;

    public IDocumentStore Store { get; }

    public NodeIdentity Identity { get; }

    public LedgerExport Export { get; }

    public LedgerImport Import { get; }

    /// <summary>An import that stops none of this node's flow runs on a lost parcel, standing in for a daemon that stopped between storing a winning claim and its stop.</summary>
    public LedgerImport ImportWithoutYield { get; }

    /// <summary>The stop of this node's flow runs on parcels another node's claim wins, which <see cref="Import"/> asks after each round.</summary>
    public LostClaims LostClaims { get; }

    /// <summary>The claim command's side of this node, publishing through <see cref="Export"/> and fetching through <see cref="Import"/>.</summary>
    public ParcelClaimant Claimant { get; }

    public RecordingLogger Logs { get; } = new();

    /// <summary>The git commands the exports this node made ran, each as its arguments after any <c>--git-dir</c>.</summary>
    public RecordingRunner ExportGit { get; } = new(Runner);

    /// <summary>The git commands the import ran, each as its arguments after any <c>--git-dir</c>.</summary>
    public RecordingRunner ImportGit { get; } = new(Runner);

    public string RefName => Export.RefName;

    /// <summary>A node with each of <paramref name="projects"/> registered at its own stand-in remote.</summary>
    public static async Task<LedgerNode> StartAsync(params string[] projects)
    {
        var temp = new TemporaryHome();
        IDocumentStore store = await SeededStore.OpenAsync(temp.Home.EnsureDatabase().DatabasePath, SoftwareDefinitions.All);
        var node = new LedgerNode(temp, store, await NodeIdentity.EnsureAsync(temp.Home, None));
        foreach (string project in projects)
        {
            await node.AddProjectAsync(project);
        }

        return node;
    }

    /// <summary>A node identity of a home of its own, unlike this node's.</summary>
    public static async Task<NodeIdentity> OtherIdentityAsync()
    {
        using var other = new TemporaryHome();
        return await NodeIdentity.EnsureAsync(other.Home, None);
    }

    /// <summary>An export of this node's store and home under <paramref name="identity"/>, logging to <see cref="Logs"/>.</summary>
    public LedgerExport ExportAs(NodeIdentity identity) =>
        new(Store, Home, identity, ExportGit, TimeProvider.System, new Logger<LedgerExport>(new LoggerFactory([Logs])));

    /// <summary>
    /// Registers <paramref name="project"/> with its one repo at <see cref="RemotePath"/> and makes its clone;
    /// the remote is made too unless <paramref name="withRemote"/> is false, so a push to it fails.
    /// </summary>
    public async Task<Project> AddProjectAsync(string project, bool withRemote = true)
    {
        if (withRemote)
        {
            await MakeRemoteAsync(project);
        }

        Project added = await new ProjectService(Store, TimeProvider.System)
            .AddAsync(Principal.LocalOwner, project, RemotePath(project), RegisteredProjects.Settings, None);
        await new RepoClones(Store, Home, Runner, NullLogger<RepoClones>.Instance).EnsureAsync(added, None);
        return added;
    }

    /// <summary>
    /// Registers <paramref name="project"/> with one repo at each of <paramref name="remotes"/>, recorded as
    /// a registration of those repos, and makes each remote, a bare repository, and each repo's clone.
    /// </summary>
    public Task<Project> AddProjectAsync(string project, IReadOnlyList<string> remotes) =>
        AddProjectAsync(project, Ids.New(), remotes);

    /// <summary>
    /// Registers <paramref name="project"/> under the project id <paramref name="id"/>, as a node holding a
    /// peer's project records it, with one repo at each of <paramref name="remotes"/>, and makes each repo's
    /// clone; each remote that is not yet a repository is made, a bare one.
    /// </summary>
    public async Task<Project> AddProjectAsync(string project, Guid id, IReadOnlyList<string> remotes)
    {
        foreach (string remote in remotes.Where(r => !Directory.Exists(r)))
        {
            Directory.CreateDirectory(remote);
            await GitAsync(remote, "init", "--bare", "--quiet");
        }

        await using (IDocumentSession session = Store.LightweightSession(project))
        {
            session.Events.StartStream<Project>(id, new ProjectRegistered(
                id, project, [.. remotes.Select(ProjectRepo.FromRemote)], Principal.LocalOwner.Id, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(None);
        }

        Project added = await ProjectAsync(project);
        await new RepoClones(Store, Home, Runner, NullLogger<RepoClones>.Instance).EnsureAsync(added, None);
        return added;
    }

    /// <summary>Seeds the node-local record of the root key the import trusts for <paramref name="project"/>, as only tests do.</summary>
    public async Task TrustRootAsync(string project, string key)
    {
        await using IDocumentSession session = Store.LightweightSession(project);
        session.Store(new LedgerRoot { Key = key });
        await session.SaveChangesAsync(None);
    }

    /// <summary>Records <paramref name="events"/>, identity events, on a stream of their own in the project's tenant.</summary>
    public async Task RecordAsync(string project, params IIdentityEvent[] events)
    {
        await using IDocumentSession session = Store.LightweightSession(project);
        session.Events.StartStream(Ids.New(), [.. events.Cast<object>()]);
        await session.SaveChangesAsync(None);
    }

    /// <summary>Records the owner root for this node's own key in the project, as the root node of it does.</summary>
    public Task RecordRootAsync(string project) =>
        RecordAsync(project, new OwnerRootRecorded(Identity.NodeId, Identity.Key.PublicKey, DateTimeOffset.UtcNow));

    /// <summary>Makes the stand-in remote of <paramref name="project"/>, a bare repository.</summary>
    public async Task MakeRemoteAsync(string project)
    {
        string remote = RemotePath(project);
        Directory.CreateDirectory(remote);
        await GitAsync(remote, "init", "--bare", "--quiet");
    }

    /// <summary>The stand-in remote of <paramref name="project"/>'s one repo, named so the repo is <see cref="RegisteredProjects.Repo"/>.</summary>
    public string RemotePath(string project) => Path.Combine(Home.Path, "remotes", project, $"{RegisteredProjects.Repo}.git");

    public string ClonePath(string project) => Home.ProjectClonePath(project);

    /// <summary>The core-made clone of <paramref name="repo"/> of <paramref name="project"/>.</summary>
    public string ClonePath(string project, string repo) => Home.RepoClonePath(project, repo);

    public async Task<Project> ProjectAsync(string project) =>
        await ProjectService.FindAsync(Store, project, None) ?? throw new InvalidOperationException($"No project '{project}'.");

    /// <summary>Every event of the project's tenant, in store order.</summary>
    public async Task<IReadOnlyList<IEvent>> EventsAsync(string project)
    {
        await using IQuerySession session = Store.QuerySession(project);
        IReadOnlyList<IEvent> events = await session.Events.QueryEventsAsync(e => e.Sequence > 0, None);
        return [.. events.OrderBy(e => e.Sequence)];
    }

    /// <summary>This store's records of every line in the project, any origin's, in sequence order.</summary>
    public async Task<IReadOnlyList<LedgerLine>> RecordsAsync(string project)
    {
        await using IQuerySession session = Store.QuerySession(project);
        IReadOnlyList<LedgerLine> records = await session.Query<LedgerLine>().ToListAsync(None);
        return [.. records.OrderBy(r => r.Origin).ThenBy(r => r.Sequence)];
    }

    /// <summary>Runs git against the repository at <paramref name="gitDirectory"/> and gives its output.</summary>
    public static Task<string> GitAsync(string gitDirectory, params string[] arguments) =>
        new Git(Runner).RunAsync(Git.Against(gitDirectory, arguments), None);

    /// <summary>Runs git against the repository at <paramref name="gitDirectory"/> with <paramref name="variables"/> beside git's own.</summary>
    public static Task<string> GitAsync(string gitDirectory, IReadOnlyDictionary<string, string> variables, params string[] arguments) =>
        new Git(Runner).RunAsync(
            Git.Against(gitDirectory, arguments) with { Environment = new Dictionary<string, string>(Git.Environment.Concat(variables)) },
            None);

    /// <summary>The commit <see cref="RefName"/> names in the repository, or null when it names none.</summary>
    public async Task<string?> RefAsync(string gitDirectory)
    {
        CommandResult result = await new Git(Runner).TryRunAsync(Git.Against(gitDirectory, "rev-parse", "--verify", "--quiet", RefName), None);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    /// <summary>The files <see cref="RefName"/> holds in the repository, by name, in name order; none when it names no commit.</summary>
    public async Task<IReadOnlyDictionary<string, string>> FilesAsync(string gitDirectory, string? revision = null)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? commit = revision ?? await RefAsync(gitDirectory);
        if (commit is null)
        {
            return files;
        }

        foreach (string entry in (await GitAsync(gitDirectory, "ls-tree", "--full-tree", commit)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split('\t');
            files[parts[1]] = await GitAsync(gitDirectory, "cat-file", "blob", parts[0].Split(' ')[2]);
        }

        return files;
    }

    /// <summary>The lines of the files <see cref="RefName"/> holds in the repository, the files read in name order.</summary>
    public async Task<IReadOnlyList<byte[]>> LinesAsync(string gitDirectory) =>
        [.. (await FilesAsync(gitDirectory)).Values
            .SelectMany(text => text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Select(Encoding.UTF8.GetBytes)];

    /// <summary>The lines the repository holds, parsed; the test fails on a line that does not parse.</summary>
    public async Task<IReadOnlyList<SignedLine>> ParsedAsync(string gitDirectory) =>
        [.. (await LinesAsync(gitDirectory)).Select(line => SignedLine.Parse(line, out string? fault) ?? throw new InvalidOperationException($"A line does not parse: {fault}"))];

    /// <summary>Closes this node's store, so a daemon can be started on its home; nothing else of the node is used after.</summary>
    public async Task CloseStoreAsync()
    {
        if (!storeClosed)
        {
            storeClosed = true;
            await Store.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Export.StopAsync(None);
        Export.Dispose();
        await Import.StopAsync(None);
        Import.Dispose();
        ImportWithoutYield.Dispose();
        await CloseStoreAsync();
        Writable(new DirectoryInfo(Home.Path));
        temp.Dispose();
    }

    /// <summary>Clears the read-only mark git leaves on its objects on Windows, so the home can be deleted.</summary>
    private static void Writable(DirectoryInfo directory)
    {
        if (!directory.Exists)
        {
            return;
        }

        foreach (FileInfo file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes &= ~FileAttributes.ReadOnly;
        }
    }
}

/// <summary>A command runner that runs every command through another and records the git commands among them.</summary>
internal sealed class RecordingRunner(ICommandRunner inner) : ICommandRunner
{
    private readonly Lock gate = new();
    private readonly List<string> calls = [];

    /// <summary>The git commands run, each as its arguments after any <c>--git-dir</c>, joined by spaces.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (gate)
            {
                return [.. calls];
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            calls.Clear();
        }
    }

    /// <summary>Whether a git command, by its arguments as <see cref="Calls"/> records them, fails without running.</summary>
    public Func<string, bool>? Refuses { get; set; }

    public Task<CommandResult> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.Program == "git")
        {
            string arguments = string.Join(' ', command.GitArguments());
            lock (gate)
            {
                calls.Add(arguments);
            }

            if (Refuses?.Invoke(arguments) == true)
            {
                return Task.FromResult(new CommandResult(1, "", "refused by the test", "refused by the test"));
            }
        }

        return inner.RunAsync(command, cancellationToken);
    }
}
