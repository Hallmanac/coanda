using Coanda.Cli;
using Coanda.Core;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.FlowRuns;

namespace Coanda.Tests.Cli;

/// <summary><c>coa flow-run resume --answer</c> carries the person's answer to the resume route, which records it trimmed or refuses it.</summary>
public sealed class FlowRunResumeCommandTests
{
    private const string Project = FlowRunResumeAnswerApiTests.Project;
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_resume_with_an_answer_records_it_on_the_visit_and_a_refused_one_records_nothing()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Guid flowRunId = await FlowRunResumeAnswerApiTests.StartedAsync(daemon, client, FlowRunResumeAnswerApiTests.TwoHolds());
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        Ran refused = await CoaProcess.RunAsync(
            temp.Home, None, "flow-run", "resume", Ids.Short(flowRunId), "--project", Project, "--answer", new string('a', 201));
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("at most 200 characters after trimming", refused.Output + refused.Error, StringComparison.Ordinal);
        Assert.Equal(paused.UpdatedAt, (await FlowRunDriver.ShowAsync(client, Project, flowRunId)).UpdatedAt);

        Ran resumed = await CoaProcess.RunAsync(
            temp.Home, None, "flow-run", "resume", Ids.Short(flowRunId), "--project", Project, "--note", "Looked it over.", "--answer", " approve ");
        Assert.True(resumed.ExitCode == 0, resumed.Output + resumed.Error);

        FlowRunView view = await FlowRunDriver.ShowAsync(client, Project, flowRunId);
        Assert.Equal("approve", view.Steps[0].ResumeAnswer);

        await daemon.StopAsync(None);
    }
}
