using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Daemon.Modules;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Sdk.Contract;
using Coanda.Sdk.Hosting;
using Coanda.Tests.Core;
using Coanda.Tests.Daemon.Modules;
using Fisher;

namespace Coanda.Tests.Daemon.SystemSteps;

/// <summary>
/// An open-pr step of a flow version from before the build flow ended at the open pull request routes on
/// <c>done</c> alone, so its <c>opened</c> is recorded as <c>done</c> when its status is asked too, though
/// the visit names the step by its prefixed name; a flow naming <c>software/open-pr</c> gets the verdict
/// the module answered. The software module answers every status unknown, so a stand-in in its place
/// answers this one done.
/// </summary>
public sealed class EarlierOpenPrStatusTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task An_open_pr_step_of_the_build_flow_before_it_ended_at_the_pull_request_has_its_opened_status_recorded_as_done()
    {
        Flow earlier = PreviousFlows.BuildWithGatedMerge;
        FlowStep openPr = earlier.Step("open-pr") ?? throw new InvalidOperationException("The flow has no open-pr step.");

        SystemStepStatus status = await StatusAsync(openPr);

        Assert.Equal("open-pr", openPr.SystemStep);
        Assert.Equal(SystemStepAnswer.DoneVerdict, Verdict(status));
    }

    [Fact]
    public async Task An_open_pr_step_naming_the_prefixed_step_has_the_verdict_the_module_answered_though_it_routes_only_on_done()
    {
        FlowStep earlier = PreviousFlows.BuildWithGatedMerge.Step("open-pr") ?? throw new InvalidOperationException("The flow has no open-pr step.");
        FlowStep prefixed = earlier with { SystemStep = SoftwareSteps.OpenPr };

        SystemStepStatus status = await StatusAsync(prefixed);

        Assert.Equal(OpenedModule.Opened, Verdict(status));
    }

    /// <summary>Asks the status of a dispatch of <paramref name="step"/> as the advancer does, by the visit's performer, which reads <c>software/open-pr</c>.</summary>
    private static async Task<SystemStepStatus> StatusAsync(FlowStep step)
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.EnsureExists().EnsureDatabase().DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, None);
        var modules = new HostedModules(new Dictionary<string, ModuleHostAdapter> { [SoftwareSteps.Module] = new(new OpenedModule()) });
        await modules.StartAsync(None);
        var systemSteps = new FlowSystemSteps([], modules, store);

        return await systemSteps.StatusAsync(Project, step, SoftwareSteps.OpenPr, Ids.New(), None);
    }

    private static string? Verdict(SystemStepStatus status) =>
        Assert.IsType<SystemStepStatus.Done>(status).Result.GetProperty(SystemStepAnswer.Verdict).GetString();

    /// <summary>A stand-in for the software module that brings open-pr and answers every status done with the verdict opened.</summary>
    private sealed class OpenedModule : IModule
    {
        public const string Opened = "opened";

        private static readonly JsonElement Schema = StepModule.Json("""{ "type": "object" }""");

        public Task HealthAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DescribeAnswer> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DescribeAnswer(
                ModuleApi.Version, "1.0.0", [new StepDescription(SoftwareSteps.OpenPr, Schema, Schema)], [], [], new SettingsSchema([])));

        public Task<DispatchAnswer> DispatchAsync(DispatchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<DispatchAnswer>(new DispatchAnswer.Done(new StepResult(Opened)));

        public Task<StatusAnswer> StatusAsync(StatusRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<StatusAnswer>(new StatusAnswer.Done(new StepResult(Opened)));

        public Task StopAsync(StopRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FeedAsync(FeedDelivery delivery, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
