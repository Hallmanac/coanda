using System.Text.Json;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Modules.Events;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
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
/// Hands each hosted module, through its host adapter's watch call, the watches its projects' published
/// triggers compile to (<see cref="WatchCompilation"/>), so it knows which external events to report:
/// one watch request for every project it is switched on for, each replacing the module's whole set for
/// that project, sent when the daemon starts and again whenever what compiles for the project changes.
/// </summary>
/// <remarks>
/// <para>
/// A module unconfigured on this node for a project is sent an empty set for it, the request that says
/// "report nothing", and so is a module switched off for a project it was sent a request for. Every
/// request is checked against the module API's watch request schema before it is sent.
/// </para>
/// <para>
/// What compiles changes only through a recorded event: a definition published or retired, a project
/// registered or its modules set, a module setting set, or a credential profile selected, whether this
/// node recorded it or the import appended it from another node's lines. The loop reads the node tenant
/// and every project's tenant past its position for those events, and on any of them compiles again every
/// module's request for every project. The position is the head of the record read before the start send
/// compiles, held in memory only, since the start send covers everything recorded before it. A profile
/// entry that appears in or leaves the module's area on disk records nothing; it is read whenever a request
/// is compiled for another reason.
/// </para>
/// <para>
/// A request identical to the last one the module accepted for the project during this run (the same
/// watches, in any order, each with its filters in any order) is not sent, and the log line for a trigger
/// left out is written only when a request is sent. Requests for one module and one project are compiled
/// and sent one at a time. A call that throws, at start or later, is logged with the module and the project
/// and tried again, compiling afresh, after a wait of one second that doubles with each further failure up
/// to five minutes; a change during the wait does not cut it short. A failure stops neither that module's
/// requests for its other projects nor any other module's, and no retry holds the daemon's start.
/// </para>
/// </remarks>
public sealed class ModuleWatches(
    IDocumentStore store,
    HostedModules modules,
    ModuleSettingsService settings,
    TimeProvider clock,
    ILogger<ModuleWatches> logger) : IHostedService, IDisposable
{
    /// <summary>The module API's watch request schema, which the daemon's project embeds from <c>schemas/module/v1/</c>.</summary>
    private const string RequestSchemaResource = "Coanda.Daemon.Modules.watch-request.schema.json";

    private const int PageSize = 256;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>The wait before the first retry of a call that threw.</summary>
    internal static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between two tries of one request.</summary>
    internal static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(5);

    /// <summary>The kinds of event that can change what a project's triggers compile to for a module.</summary>
    private static readonly Type[] ChangeEvents =
    [
        typeof(DefinitionPublished),
        typeof(DefinitionRetired),
        typeof(ProjectRegistered),
        typeof(ProjectModulesSet),
        typeof(ModuleSettingSet),
        typeof(ModuleProfileSelected),
    ];

    private static readonly Lazy<Task<JsonSchema>> RequestSchema = new(ReadRequestSchemaAsync);

    private readonly DefinitionResolver definitions = new(store);
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private readonly Dictionary<(string Module, string Project), Pair> pairs = [];
    private readonly List<Task> attempts = [];
    private Task running = Task.CompletedTask;
    private long position;

    /// <summary>
    /// Fixes the loop's position at the head of the record, then sends every hosted module, in name order,
    /// one watch request for each project it is switched on for, in name order. The daemon calls this once
    /// the modules have described themselves and their definitions have been imported, and before it writes
    /// its address file. A request that fails is logged and left to the loop to send again once
    /// <see cref="Begin"/> starts it.
    /// </summary>
    public async Task SendAllAsync(CancellationToken cancellationToken)
    {
        // Before anything compiles, so whatever is recorded while the start send runs is read past the position.
        position = await store.Database.FetchHighestEventSequenceNumber(cancellationToken);
        IReadOnlyList<Project> projects = await ProjectsAsync(cancellationToken);
        foreach (string module in Described())
        {
            foreach (Project project in projects.Where(p => p.IsSwitchedOn(module)).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                Pair pair = PairOf(module, project.Name);
                lock (gate)
                {
                    pair.Running = true;
                    pair.Due = false;
                }

                await AttemptAsync(pair, cancellationToken);

                // A stopped start is not a failed request, which the attempt logs and leaves for the loop.
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>Starts the loop that sends a request again when what compiles changes or a call that threw is due again.</summary>
    public void Begin() => running = Task.Run(() => RunAsync(stopping.Token));

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.CancelAsync();
        Task[] inFlight;
        lock (gate)
        {
            inFlight = [running, .. attempts];
        }

        try
        {
            await Task.WhenAll(inFlight).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The daemon stopped before the module watches finished the requests they were sending.");
        }
    }

    public void Dispose() => stopping.Dispose();

    /// <summary>
    /// The same key for two requests that name the same project and the same watches, in any order, each
    /// with its filters in any order.
    /// </summary>
    internal static string KeyOf(WatchRequest request) =>
        JsonSerializer.Serialize(new object[]
        {
            request.Project,
            request.Watches
                .Select(w => JsonSerializer.Serialize(new object[] { w.Kind, w.Filters.Select(f => new[] { f.Field, f.Value }).OrderBy(f => f[0], StringComparer.Ordinal).ThenBy(f => f[1], StringComparer.Ordinal) }))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        });

    /// <summary>The wait after the <paramref name="failures"/>th failure in a row: one second, doubling, at most five minutes.</summary>
    internal static TimeSpan RetryAfter(int failures)
    {
        TimeSpan wait = FirstRetry;
        for (int i = 1; i < failures && wait < LongestRetry; i++)
        {
            wait *= 2;
        }

        return wait < LongestRetry ? wait : LongestRetry;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await ChangedAsync(cancellationToken))
                {
                    await MarkAllDueAsync(cancellationToken);
                }

                StartDue(cancellationToken);
                await Task.Delay(PollInterval, clock, cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure)
            {
                // Reading the record failed; the position did not move, so the next poll reads it again.
                logger.LogError(failure, "The module watches could not read the record past sequence {Sequence}.", position);
                await Task.Delay(PollInterval, clock, CancellationToken.None);
            }
        }
    }

    /// <summary>Whether the record past the position holds an event that can change what compiles; moves the position to the head it read.</summary>
    private async Task<bool> ChangedAsync(CancellationToken cancellationToken)
    {
        // The head is read before the projects are, so every event at or below it was committed with its
        // project's documents already in place: a project registered past the position is read with it.
        long head = await store.Database.FetchHighestEventSequenceNumber(cancellationToken);
        if (head <= position)
        {
            return false;
        }

        string[] names = [.. ChangeEvents.Select(t => store.Options.EventGraph.EventMappingFor(t).EventTypeName)];
        string[] tenants = [Tenants.Node, .. (await ProjectsAsync(cancellationToken)).Select(p => p.Name)];
        bool changed = false;
        for (long from = position; from < head && !changed; from += PageSize)
        {
            long start = from;
            long to = Math.Min(from + PageSize, head);
            foreach (string tenant in tenants)
            {
                await using IQuerySession session = store.QuerySession(tenant);
                IReadOnlyList<IEvent> events = await session.Events.QueryEventsAsync(
                    e => e.Sequence > start && e.Sequence <= to && names.Contains(e.EventTypeName), cancellationToken);
                if (events.Count > 0)
                {
                    changed = true;
                    break;
                }
            }
        }

        position = head;
        return changed;
    }

    /// <summary>
    /// Marks due every module's request for every project it is switched on for, and for every project it
    /// was sent one for this run and is now switched off for, which then compiles to the empty set.
    /// </summary>
    private async Task MarkAllDueAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Project> projects = await ProjectsAsync(cancellationToken);
        foreach (string module in Described())
        {
            foreach (Project project in projects.Where(p => p.IsSwitchedOn(module)))
            {
                PairOf(module, project.Name);
            }
        }

        lock (gate)
        {
            foreach (Pair pair in pairs.Values)
            {
                pair.Due = true;
            }
        }
    }

    /// <summary>Starts, each on a worker of its own, every due request no call is running for and no retry wait holds back.</summary>
    private void StartDue(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        lock (gate)
        {
            attempts.RemoveAll(t => t.IsCompleted);
            foreach (Pair pair in pairs.Values.Where(p => p.Due && !p.Running && (p.RetryAt is not { } at || at <= now)))
            {
                pair.Running = true;
                pair.Due = false;
                attempts.Add(Task.Run(() => AttemptAsync(pair, cancellationToken), CancellationToken.None));
            }
        }
    }

    /// <summary>
    /// Compiles the pair's request and sends it unless the module already accepted one identical to it. A
    /// failure, in compiling or in the module's call, is logged and makes the pair due again once its wait has
    /// passed; a daemon stop is not a failure. The caller has marked the pair running.
    /// </summary>
    private async Task AttemptAsync(Pair pair, CancellationToken cancellationToken)
    {
        try
        {
            if (modules.AdapterOf(pair.Module) is not { } adapter || modules.DescribeOf(pair.Module) is not { } describe)
            {
                lock (gate)
                {
                    pair.Running = false;
                }

                return;
            }

            (CompiledWatches compiled, IReadOnlyList<string>? missing) = await CompileAsync(pair, describe, cancellationToken);
            string key = KeyOf(compiled.Request);
            string? accepted;
            lock (gate)
            {
                accepted = pair.Accepted;
            }

            if (string.Equals(key, accepted, StringComparison.Ordinal))
            {
                Succeeded(pair, key);
                return;
            }

            if (missing is not null)
            {
                logger.LogInformation(
                    "Sent module '{Module}' no watches for project '{Project}', for which it is unconfigured on this node; it needs {Missing}.",
                    pair.Module, pair.Project, string.Join(", ", missing));
            }

            foreach (DroppedTrigger dropped in compiled.Dropped)
            {
                logger.LogWarning(
                    "Left trigger '{Trigger}' of project '{Project}' out of the watch request to module '{Module}': {Reason}.",
                    dropped.Trigger, pair.Project, pair.Module, dropped.Reason);
            }

            await adapter.WatchAsync(compiled.Request, cancellationToken);
            Succeeded(pair, key);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            lock (gate)
            {
                pair.Running = false;
            }
        }
        catch (Exception failure)
        {
            TimeSpan wait;
            lock (gate)
            {
                pair.Failures++;
                wait = RetryAfter(pair.Failures);
                pair.RetryAt = clock.GetUtcNow() + wait;
                pair.Due = true;
                pair.Running = false;
            }

            logger.LogError(
                failure, "The watch request for project '{Project}' to module '{Module}' failed; it is sent again in {Wait}.",
                pair.Project, pair.Module, wait);
        }
    }

    /// <summary>
    /// The request for the pair as it compiles now: the empty set when the module is switched off for the
    /// project or unconfigured there, and otherwise what the project's resolved triggers compile to; with the
    /// settings it needs when it is unconfigured.
    /// </summary>
    private async Task<(CompiledWatches Compiled, IReadOnlyList<string>? Missing)> CompileAsync(Pair pair, DescribeAnswer describe, CancellationToken cancellationToken)
    {
        JsonSchema schema = await RequestSchema.Value.WaitAsync(cancellationToken);
        var none = new CompiledWatches(new WatchRequest(pair.Project, []), []);
        Project? project = await ProjectService.FindAsync(store, pair.Project, cancellationToken);
        CompiledWatches compiled;
        IReadOnlyList<string>? missing = null;
        if (project is null || !project.IsSwitchedOn(pair.Module))
        {
            compiled = none;
        }
        else if (await settings.StateAsync(project.Name, pair.Module, cancellationToken) is { State: ModuleState.Unconfigured } configuration)
        {
            compiled = none;
            missing = configuration.Missing;
        }
        else
        {
            IReadOnlyList<Definition> triggers = await definitions.ResolveAllAsync(project.Name, DefinitionKind.Trigger, cancellationToken);
            compiled = WatchCompilation.Compile(project, pair.Module, describe, triggers, schema);
        }

        ICollection<ValidationError> failures = schema.Validate(JsonSerializer.Serialize(compiled.Request, ModuleJson.Options));
        return failures.Count == 0
            ? (compiled, missing)
            : throw new InvalidOperationException(
                $"The watch request breaks the module API's watch request schema: {string.Join("; ", failures.Select(f => $"{f.Kind} at {f.Path ?? "#"}"))}.");
    }

    private void Succeeded(Pair pair, string key)
    {
        lock (gate)
        {
            pair.Accepted = key;
            pair.Failures = 0;
            pair.RetryAt = null;
            pair.Running = false;
        }
    }

    /// <summary>Every hosted module that has described itself, in ordinal name order.</summary>
    private IEnumerable<string> Described() =>
        modules.Names
            .Where(m => modules.AdapterOf(m) is not null && modules.DescribeOf(m) is not null)
            .Order(StringComparer.Ordinal);

    private Pair PairOf(string module, string project)
    {
        lock (gate)
        {
            if (!pairs.TryGetValue((module, project), out Pair? pair))
            {
                pair = new Pair(module, project);
                pairs.Add((module, project), pair);
            }

            return pair;
        }
    }

    private async Task<IReadOnlyList<Project>> ProjectsAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession(Tenants.Node);
        return await query.Query<Project>().AnyTenant().ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the embedded watch request schema once for the whole process. It takes no cancellation token
    /// on purpose: <see cref="RequestSchema"/> caches the task it returns, so a token one caller cancelled
    /// would leave every later read with a cancelled schema.
    /// </summary>
    private static async Task<JsonSchema> ReadRequestSchemaAsync()
    {
        await using Stream stream = typeof(ModuleWatches).Assembly.GetManifestResourceStream(RequestSchemaResource)
            ?? throw new InvalidOperationException($"The daemon carries no '{RequestSchemaResource}'.");
        return await JsonSchema.FromJsonAsync(stream, CancellationToken.None);
    }

    /// <summary>
    /// One module's requests for one project: the key of the last request it accepted this run, whether one
    /// is due to be compiled, whether a call is running, and the failures in a row with when the next try may
    /// start. Read and written only under the service's lock.
    /// </summary>
    private sealed class Pair(string module, string project)
    {
        public string Module { get; } = module;

        public string Project { get; } = project;

        public string? Accepted { get; set; }

        public bool Due { get; set; }

        public bool Running { get; set; }

        public int Failures { get; set; }

        public DateTimeOffset? RetryAt { get; set; }
    }
}
