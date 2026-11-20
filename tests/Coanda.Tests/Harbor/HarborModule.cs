using System.Text.Json;
using Coanda.Daemon.Modules;
using Coanda.Sdk.Client;
using Coanda.Sdk.Contract;
using Coanda.Sdk.Hosting;

namespace Coanda.Tests.Harbor;

/// <summary>
/// A module that knows nothing of software: it keeps a harbor. Its directory (<see cref="Path"/>) holds its
/// <c>module.json</c> and its definitions folder: the parcel types vessel, crate, and logbook, the flows dock,
/// tally, unload, and recount, the agents surveyor and clerk, and the triggers arrival and docked. It describes
/// the event kinds <see cref="Arrival"/> and <see cref="Cleared"/>, answers its one system step
/// <see cref="Weigh"/> through dispatch, and records events and reads flow runs through the SDK's core client
/// with the address and token the daemon gives it once it listens. The daemon hosts it composed, as the host
/// that builds the executable composes the software module (<see cref="Compose"/>).
/// </summary>
internal sealed class HarborModule : IModule, IDisposable
{
    public const string Name = "harbor";

    /// <summary>The kind the arrival trigger creates a vessel on, titled by the event's <c>name</c> field.</summary>
    public const string Arrival = "harbor/arrival";

    /// <summary>The kind the dock flow's clearance step waits for.</summary>
    public const string Cleared = "harbor/cleared";

    /// <summary>The module's own system step, which answers done with the tonnage <see cref="Tonnage"/>.</summary>
    public const string Weigh = "harbor/weigh";

    public const int Tonnage = 1200;

    private static readonly JsonElement ArgumentSchema = Json("""
        { "type": "object", "required": ["scale"], "properties": { "scale": { "type": "string" } }, "additionalProperties": false }
        """);

    private static readonly JsonElement ResultSchema = Json("""
        { "type": "object", "required": ["verdict", "tonnage"], "properties": { "verdict": { "type": "string" }, "tonnage": { "type": "integer" } } }
        """);

    private readonly HttpClient http = new();
    private readonly Lock gate = new();
    private readonly List<DispatchRequest> dispatches = [];
    private Func<CoreApiOptions>? coreApi;

    /// <summary>The directory the build copies the module's <c>module.json</c> and definitions folder to.</summary>
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Harbor", "module");

    /// <summary>Every dispatch of <see cref="Weigh"/> received, in the order received.</summary>
    public IReadOnlyList<DispatchRequest> Dispatches
    {
        get
        {
            lock (gate)
            {
                return [.. dispatches];
            }
        }
    }

    /// <summary>This module, composed under its name from its directory, built once at start from what the daemon gives it.</summary>
    public ComposedModule Compose() =>
        ComposedModule.Of(Name, Path, context =>
        {
            coreApi = context.CoreApi;
            return this;
        });

    /// <summary>Records <paramref name="recorded"/> through the core client, as the module reporting something it saw.</summary>
    public Task<CoreExternalEvent> RecordAsync(RecordedEvent recorded, CancellationToken cancellationToken) =>
        Client().RecordEventAsync(recorded, cancellationToken);

    /// <summary>The flow runs of <paramref name="project"/> started under the flow whose hash is <paramref name="flowHash"/>, read through the core client.</summary>
    public Task<IReadOnlyList<CoreFlowRun>> FlowRunsAsync(string project, string flowHash, CancellationToken cancellationToken) =>
        Client().ProjectFlowRunsAsync(project, flowHash, null, cancellationToken);

    public Task HealthAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<DescribeAnswer> DescribeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new DescribeAnswer(
            ModuleApi.Version,
            "1.0.0",
            [new StepDescription(Weigh, ArgumentSchema, ResultSchema)],
            [new EventKindDescription(Arrival, ["name"]), new EventKindDescription(Cleared, [])],
            [],
            new SettingsSchema([])));

    public Task<DispatchAnswer> DispatchAsync(DispatchRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Step, Weigh, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The harbor module brings the one step '{Weigh}', not '{request.Step}'.");
        }

        lock (gate)
        {
            dispatches.Add(request);
        }

        return Task.FromResult<DispatchAnswer>(new DispatchAnswer.Done(
            new StepResult("done") { Fields = new Dictionary<string, JsonElement> { ["tonnage"] = JsonSerializer.SerializeToElement(Tonnage) } }));
    }

    public Task<StatusAnswer> StatusAsync(StatusRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<StatusAnswer>(new StatusAnswer.Unknown());

    public Task StopAsync(StopRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task WatchAsync(WatchRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FeedAsync(FeedDelivery delivery, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => http.Dispose();

    private CoreApiClient Client() =>
        new(http, (coreApi ?? throw new InvalidOperationException("The daemon has not built the harbor module yet.")).Invoke());

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
