using Coanda.Daemon.Executors.ClaudeCode;
using Microsoft.Extensions.Hosting;

namespace Coanda.Daemon.Executors;

/// <summary>
/// Ends every session the daemon's executor still holds once the flow runner has stopped, for an
/// executor that cannot reattach to one across a restart. Registered before the runner, so it stops
/// after it: the runner's stop leaves a judgment's recorded session alive, and nothing after this
/// would end it.
/// </summary>
public sealed class ExecutorShutdown(IAgentExecutor executor) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (executor is ClaudeCodeExecutor claude)
        {
            try
            {
                await claude.StopAllAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Every kill was sent before the wait; the host gave up waiting for the processes to end.
            }
        }
    }
}
