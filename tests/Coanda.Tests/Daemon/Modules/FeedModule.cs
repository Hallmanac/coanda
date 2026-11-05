using System.Text.Json;
using Coanda.Sdk.Contract;
using Coanda.Sdk.Hosting;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// A stand-in module the daemon hosts in its process, which brings no steps, subscribes to
/// <paramref name="subscriptions"/>, declares <paramref name="settings"/> (none required when null), and
/// records every feed delivery it receives as the JSON the module API defines. <see cref="OnFeed"/> runs
/// on each delivery once it is recorded, so a test can hold or fail one.
/// </summary>
internal sealed class FeedModule(IReadOnlyList<CoreEventKind> subscriptions, SettingsSchema? settings = null) : IModule
{
    private readonly Lock gate = new();
    private readonly List<FeedDelivery> deliveries = [];
    private readonly List<string> documents = [];

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
        Task.FromResult(new DescribeAnswer(ModuleApi.Version, "1.0.0", [], [], subscriptions, settings ?? new SettingsSchema([])));

    public Task<DispatchAnswer> DispatchAsync(DispatchRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The feed module brings no steps.");

    public Task<StatusAnswer> StatusAsync(StatusRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<StatusAnswer>(new StatusAnswer.Unknown());

    public Task StopAsync(StopRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

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
