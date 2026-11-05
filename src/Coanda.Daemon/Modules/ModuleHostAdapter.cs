using Coanda.Modules.Software;
using Coanda.Sdk.Contract;

namespace Coanda.Daemon.Modules;

/// <summary>
/// The one host adapter through which the daemon calls the software module it hosts in its own
/// process. It offers only calls the module API's index of calls defines, each method named after
/// the call it carries, and takes and returns the API's own documents, so nothing reaches the module
/// that a module in a process of its own would not receive. A test holds its methods to the index.
/// The feed delivery and the watch request join it with the work that sends them.
/// </summary>
public sealed class ModuleHostAdapter(SoftwareModule module)
{
    /// <summary>health: completes once the module answers.</summary>
    public Task HealthAsync(CancellationToken cancellationToken) => module.HealthAsync(cancellationToken);

    /// <summary>describe: the module's steps, event kinds, subscriptions, and settings schema.</summary>
    public Task<DescribeAnswer> DescribeAsync(CancellationToken cancellationToken) => module.DescribeAsync(cancellationToken);

    /// <summary>dispatch: one system step.</summary>
    public Task<DispatchAnswer> DispatchAsync(DispatchRequest request, CancellationToken cancellationToken) =>
        module.DispatchAsync(request, cancellationToken);

    /// <summary>status: where a dispatch stands.</summary>
    public Task<StatusAnswer> StatusAsync(StatusRequest request, CancellationToken cancellationToken) =>
        module.StatusAsync(request, cancellationToken);

    /// <summary>stop: the dispatch is to stop, because a person stopped its flow run or a resume replaced it.</summary>
    public Task StopAsync(StopRequest request, CancellationToken cancellationToken) => module.StopAsync(request, cancellationToken);
}
