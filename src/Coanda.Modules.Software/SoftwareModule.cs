using Coanda.Modules.Software.GitHub;
using Coanda.Modules.Software.Repositories;
using Coanda.Modules.Software.Steps;
using Coanda.Sdk.Client;
using Coanda.Sdk.Contract;
using Coanda.Sdk.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Coanda.Modules.Software;

/// <summary>
/// The software engineering module, answering the module API's calls on it: health, describe,
/// dispatch, status, stop, and the feed. A dispatch carries only the dispatch request; the step reads the
/// project's record, the module's settings in the project, the parcel, and the flow runs on it through
/// the core's API, presenting the module's token, and writes the task's repos and pull request fields
/// the same way. It reaches GitHub and the core
/// through typed clients of its own <see cref="IHttpClientFactory"/>, and the daemon only through the
/// module API's calls and the core's API.
/// </summary>
/// <remarks>
/// A flow run's worktree is the module's to remove. The feed delivers the end or stop of every flow run
/// in a project the module is switched on for, only once nothing the core runs for it is still running
/// in its work path, and the module removes that flow run's worktree and keeps its branch. A removal
/// that fails, or a delivery the core skipped, leaves the worktree to the next start: the module's first
/// health call sweeps the worktrees of every flow run that has ended or been stopped before it answers,
/// and the core waits on health before it advances any live flow run. A worktree step that needs such a
/// worktree's branch before then takes it over once the core shows its flow run no longer working.
/// </remarks>
public sealed class SoftwareModule : IModule, IAsyncDisposable
{
    private readonly ServiceProvider services;
    private readonly Dictionary<string, ISoftwareStep> steps;
    private readonly ProjectClones clones;
    private readonly FlowRunEnds ends = new();
    private readonly ILogger<SoftwareModule> logger;
    private readonly SemaphoreSlim starting = new(1, 1);
    private bool swept;

    public SoftwareModule(SoftwareModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var collection = new ServiceCollection();
        collection.AddSingleton(options.LoggerFactory);
        collection.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        GitHubClient.Register(collection, options.GitHubHandler);
        collection.AddHttpClient<CoreApiClient>().AddTypedClient((http, _) => new CoreApiClient(http, options.CoreApi()));
        services = collection.BuildServiceProvider();

        var paths = new ClonePaths(options.ClonesPath, options.WorkPath);
        clones = new ProjectClones(paths, options.CommandRunner, options.LoggerFactory.CreateLogger<ProjectClones>());
        logger = options.LoggerFactory.CreateLogger<SoftwareModule>();
        var tokens = new GitHubToken(options.CommandRunner, options.HomePath, options.ProfilesPath);

        // A typed client is resolved for each use, so it never holds a handler past its rotation.
        GitHubClient GitHub() => services.GetRequiredService<GitHubClient>();
        steps = new Dictionary<string, ISoftwareStep>(StringComparer.Ordinal);
        foreach (ISoftwareStep step in new ISoftwareStep[]
        {
            new WorktreeStep(clones, ends),
            new GatesStep(paths, options.CommandRunner, options.GatesCommandLimit),
            new OpenPrStep(clones, tokens, GitHub),
            new GatedMergeStep(tokens, GitHub),
            new RebaseStep(clones),
        })
        {
            steps.Add(step.Name, step);
        }
    }

    /// <summary>
    /// Health: the module answers once its start-up sweep has run, which the first call runs. The sweep
    /// removes the worktree of every flow run an existing clone lists that the core shows ended or stopped,
    /// noting each such flow run's end even where its removal fails, and leaves the rest. A sweep that fails
    /// is logged and the module answers all the same; a cancelled one runs again on the next call.
    /// </summary>
    public async Task HealthAsync(CancellationToken cancellationToken)
    {
        await starting.WaitAsync(cancellationToken);
        try
        {
            if (!swept)
            {
                await SweepAsync(cancellationToken);
                swept = true;
            }
        }
        finally
        {
            starting.Release();
        }
    }

    /// <summary>Describe: the module's steps, with their argument and result schemas, and its settings schema.</summary>
    public Task<DescribeAnswer> DescribeAsync(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<DescribeAnswer>(cancellationToken)
            : Task.FromResult(SoftwareDescribe.Answer);

    /// <summary>
    /// Dispatch: does one step under the request's dispatch id. A step the module does not bring is
    /// answered with a pause, since the daemon dispatches only what describe lists.
    /// </summary>
    public async Task<DispatchAnswer> DispatchAsync(DispatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = new StepContext(request, services.GetRequiredService<CoreApiClient>());
        return steps.TryGetValue(request.Step, out ISoftwareStep? step)
            ? await step.DispatchAsync(context, cancellationToken)
            : await context.PerformAgainAsync($"names the step '{request.Step}', which module '{SoftwareSteps.Module}' does not bring.", cancellationToken);
    }

    /// <summary>Status: none of the module's steps answers running, so it knows no dispatch and answers unknown.</summary>
    public Task<StatusAnswer> StatusAsync(StatusRequest request, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<StatusAnswer>(cancellationToken)
            : Task.FromResult<StatusAnswer>(new StatusAnswer.Unknown());

    /// <summary>Stop: none of the module's steps answers running, so nothing of a dispatch is left running to stop.</summary>
    public Task StopAsync(StopRequest request, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// Feed: for each flow run that ended or was stopped, notes its end, which lets a worktree step waiting
    /// to take its worktree over go on, then removes its worktree and keeps its branch. A delivery the
    /// module has seen before, one whose worktree another flow run's worktree step has already taken over,
    /// and one in a project whose clone is gone remove nothing. A removal that fails is logged and left to
    /// the next start's sweep.
    /// </summary>
    public async Task FeedAsync(FeedDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        foreach (FeedEvent feedEvent in delivery.Events)
        {
            Guid? final = feedEvent switch
            {
                FeedEvent.FlowRunEnded ended => ended.FlowRunId,
                FeedEvent.FlowRunStopped stopped => stopped.FlowRunId,
                _ => null,
            };
            if (final is Guid flowRunId)
            {
                ends.Note(flowRunId);
                await RemoveWorktreeAsync(feedEvent.Project, flowRunId, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        starting.Dispose();
        await services.DisposeAsync();
    }

    private async Task RemoveWorktreeAsync(string project, Guid flowRunId, CancellationToken cancellationToken)
    {
        try
        {
            if (await clones.RemoveAsync(project, flowRunId, cancellationToken))
            {
                logger.LogInformation("Removed the worktree of flow run {FlowRun} in project {Project}.", ClonePaths.Short(flowRunId), project);
            }

            // The worktree is gone, removed now or before, so no step can find it holding a branch.
            ends.Forget(flowRunId);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            logger.LogError(failure, "Removing the worktree of flow run {FlowRun} in project {Project} failed; the next start sweeps it.", ClonePaths.Short(flowRunId), project);
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        CoreApiClient core = services.GetRequiredService<CoreApiClient>();
        try
        {
            await clones.SweepAsync(
                async (project, flowRunId, token) =>
                {
                    if (!await IsFinalAsync(core, project, flowRunId, token))
                    {
                        return false;
                    }

                    ends.Note(flowRunId);
                    return true;
                },
                cancellationToken);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(failure, "Sweeping the worktrees of ended and stopped flow runs failed; the module starts without it.");
        }
    }

    /// <summary>Whether the core shows the flow run ended or stopped; one it shows no such flow run of, or refuses to show, is not.</summary>
    private static async Task<bool> IsFinalAsync(CoreApiClient core, string project, Guid flowRunId, CancellationToken cancellationToken)
    {
        try
        {
            return (await core.FlowRunAsync(project, flowRunId, cancellationToken)).IsFinal;
        }
        catch (CoreApiException)
        {
            return false;
        }
    }
}
