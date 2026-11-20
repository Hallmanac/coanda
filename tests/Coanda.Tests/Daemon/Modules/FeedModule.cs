using System.Text.Json;
using Coanda.Sdk.Contract;
using Coanda.Sdk.Hosting;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// A stand-in module the daemon hosts in its process, which brings no steps, subscribes to
/// <paramref name="subscriptions"/>, declares <paramref name="settings"/> (none required when null),
/// describes <paramref name="eventKinds"/> (none when null), asks for its feed from <paramref name="feedFrom"/>
/// (leaving the ask out when null), and records every feed delivery and every
/// watch request it receives as the JSON the module API defines. <see cref="OnFeed"/> runs on each
/// delivery once it is recorded, so a test can hold or fail one, and <see cref="OnWatch"/> on each watch
/// request once it is recorded.
/// </summary>
internal sealed class FeedModule(
    IReadOnlyList<CoreEventKind> subscriptions,
    SettingsSchema? settings = null,
    IReadOnlyList<EventKindDescription>? eventKinds = null,
    FeedStart? feedFrom = null) : IModule
{
    private readonly Lock gate = new();
    private readonly List<FeedDelivery> deliveries = [];
    private readonly List<string> documents = [];
    private readonly List<WatchRequest> watchRequests = [];
    private readonly List<string> watchDocuments = [];

    /// <summary>What runs on each watch request after it is recorded; none by default.</summary>
    public Func<WatchRequest, CancellationToken, Task>? OnWatch { get; set; }

    /// <summary>Every watch request received, in the order received.</summary>
    public IReadOnlyList<WatchRequest> WatchRequests
    {
        get
        {
            lock (gate)
            {
                return [.. watchRequests];
            }
        }
    }

    /// <summary>Every watch request received, as the module API's JSON.</summary>
    public IReadOnlyList<string> WatchDocuments
    {
        get
        {
            lock (gate)
            {
                return [.. watchDocuments];
            }
        }
    }

    /// <summary>What runs on each delivery after it is recorded; none by default.</summary>
    public Func<FeedDelivery, CancellationToken, Task>? OnFeed { get; set; }

    /// <summary>Every delivery received, in the order received.</summary>
    public IReadOnlyList<FeedDelivery> Deliveries
    {
        get
        {
            lock (gate)
            {
                return [.. deliveries];
            }
        }
    }

    /// <summary>Every delivery received, as the module API's JSON.</summary>
    public IReadOnlyList<string> Documents
    {
        get
        {
            lock (gate)
            {
                return [.. documents];
            }
        }
    }

    /// <summary>Every event received, in the order received.</summary>
    public IReadOnlyList<FeedEvent> Events => [.. Deliveries.SelectMany(d => d.Events)];

    public Task HealthAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<DescribeAnswer> DescribeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new DescribeAnswer(ModuleApi.Version, "1.0.0", [], eventKinds ?? [], subscriptions, settings ?? new SettingsSchema([]), feedFrom));

    public Task<DispatchAnswer> DispatchAsync(DispatchRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The feed module brings no steps.");

    public Task<StatusAnswer> StatusAsync(StatusRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<StatusAnswer>(new StatusAnswer.Unknown());

    public Task StopAsync(StopRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task WatchAsync(WatchRequest request, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            watchRequests.Add(request);
            watchDocuments.Add(JsonSerializer.Serialize(request, ModuleJson.Options));
        }

        if (OnWatch is { } onWatch)
        {
            await onWatch(request, cancellationToken);
        }
    }

    public async Task FeedAsync(FeedDelivery delivery, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            deliveries.Add(delivery);
            documents.Add(JsonSerializer.Serialize(delivery, ModuleJson.Options));
        }

        if (OnFeed is { } onFeed)
        {
            await onFeed(delivery, cancellationToken);
        }
    }
}
