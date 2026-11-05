using Coanda.Modules.Software.GitHub;
using Coanda.Modules.Software.Repositories;
using Coanda.Modules.Software.Steps;
using Coanda.Sdk.Client;
using Coanda.Sdk.Contract;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Coanda.Modules.Software;

/// <summary>
/// The software engineering module, answering the module API's calls on it: health, describe,
/// dispatch, status, and stop. A dispatch carries only the dispatch request; the step reads the
/// project's record, the parcel, and the flow runs on it through the core's API, presenting the
/// module's token, and writes the pull request field the same way. It reaches GitHub and the core
/// through typed clients of its own <see cref="IHttpClientFactory"/>, and the daemon only through the
/// module API and the interim seam (<see cref="IInterimWorktrees"/>), where it serves the daemon's
/// worktree removal and start-up sweep.
/// </summary>
public sealed class SoftwareModule : IAsyncDisposable
{
    private readonly ServiceProvider services;
    private readonly Dictionary<string, ISoftwareStep> steps;

    public SoftwareModule(SoftwareModuleOptions options, IInterimWorktrees interim)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(interim);
        var collection = new ServiceCollection();
        collection.AddSingleton(options.LoggerFactory);
        collection.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        GitHubClient.Register(collection, options.GitHubHandler);
        collection.AddHttpClient<CoreApiClient>().AddTypedClient((http, _) => new CoreApiClient(http, options.CoreApi()));
        services = collection.BuildServiceProvider();

        var paths = new ClonePaths(options.ClonesPath, options.WorkPath);
        var clones = new ProjectClones(paths, options.CommandRunner, options.LoggerFactory.CreateLogger<ProjectClones>());
        var tokens = new GitHubToken(options.CommandRunner, options.HomePath);

        // A typed client is resolved for each use, so it never holds a handler past its rotation.
        GitHubClient GitHub() => services.GetRequiredService<GitHubClient>();
        steps = new Dictionary<string, ISoftwareStep>(StringComparer.Ordinal);
        foreach (ISoftwareStep step in new ISoftwareStep[]
        {
            new WorktreeStep(clones, interim),
            new GatesStep(paths, options.CommandRunner, options.GatesCommandLimit),
            new OpenPrStep(clones, tokens, GitHub),
            new GatedMergeStep(tokens, GitHub),
            new RebaseStep(clones),
        })
        {
            steps.Add(step.Name, step);
        }

        interim.OnRemove(clones.RemoveAsync);
        interim.OnSweep(clones.SweepAsync);
    }

    /// <summary>Health: an in-process module is healthy once it is built.</summary>
    public Task HealthAsync(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : Task.CompletedTask;

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

    public ValueTask DisposeAsync() => services.DisposeAsync();
}
