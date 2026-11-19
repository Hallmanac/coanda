using Coanda.Sdk.Contract;

namespace Coanda.Sdk.Hosting;

/// <summary>
/// A module as the daemon calls it while hosting it in its own process: one method for each call of the
/// module API's index of calls that the host sends today, each taking and returning the API's own
/// documents, so a module hosted this way receives nothing a module in a process of its own would not.
/// </summary>
public interface IModule
{
    /// <summary>health: completes once the module answers.</summary>
    Task HealthAsync(CancellationToken cancellationToken);

    /// <summary>describe: the module's steps, event kinds, subscriptions, and settings schema.</summary>
    Task<DescribeAnswer> DescribeAsync(CancellationToken cancellationToken);

    /// <summary>dispatch: one system step.</summary>
    Task<DispatchAnswer> DispatchAsync(DispatchRequest request, CancellationToken cancellationToken);

    /// <summary>status: where a dispatch stands.</summary>
    Task<StatusAnswer> StatusAsync(StatusRequest request, CancellationToken cancellationToken);

    /// <summary>stop: the dispatch is to stop, because a person stopped its flow run or a resume replaced it.</summary>
    Task StopAsync(StopRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// watch: the watches the core compiled from one project's triggers, replacing the module's whole set
    /// for that project, so an empty list means the module reports nothing for it. It has no answer.
    /// </summary>
    Task WatchAsync(WatchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// feed: core events the module's describe subscribes to, in sequence order. The module has handled
    /// the delivery once this completes; one that throws has failed it, and the core passes it over.
    /// </summary>
    Task FeedAsync(FeedDelivery delivery, CancellationToken cancellationToken);
}
