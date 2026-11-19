using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Store;
using Fisher;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// A wait-for-human step resumed with a person's answer: the answer in the step's result, the flow
/// routing on it, the re-pause when the step routes on an answer and none was given, the refusal of
/// an answer the step would not route on, and the answer carried across a daemon restart.
/// </summary>
public sealed class WaitForHumanAnswerFlowTests
{
    private const string Project = FlowRunResumeAnswerApiTests.Project;
    private static readonly CancellationToken None = CancellationToken.None;

    private static FlowTransition On(string field, string value, string target) =>
        new() { Field = field, Value = FlowValue.Of(value), VisitCap = 2, Target = target };

    private static FlowStep Hold(string name, params FlowTransition[] transitions) => new()
    {
        Name = name,
        SystemStep = CoreSystemSteps.WaitForHuman,
        Arguments = new Dictionary<string, JsonElement> { ["lever"] = FlowValue.Of($"Answer {name}.") },
        Transitions = transitions,
    };

    /// <summary>A step that asks whether to pursue, going on to a later step that routes on no answer, or to stop.</summary>
    private static Flow PursueOrStop() => new()
    {
        Name = "pursue-or-stop",
        Accepts = ["task"],
        Outcome = "decided",
        Budget = 1m,
        SpawnCap = 1,
        Steps =
        [
            Hold("ask", On("answer", "pursue", "later"), On("answer", "stop", Flow.End)),
            Hold("later", On("verdict", "done", Flow.End)),
        ],
    };

    [Fact]
    public async Task An_answer_of_pursue_records_it_in_the_result_and_goes_to_the_later_step_where_no_answer_records_todays_result()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Guid flowRunId = await FlowRunResumeAnswerApiTests.StartedAsync(daemon, client, PursueOrStop());
        await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, null, " pursue ");
        FlowRunView later = await UntilPausedAtAsync(client, flowRunId, "later");

        Assert.Equal(["ask", "later"], later.Steps.Select(s => s.Step));
        AssertResult(later.Steps[0].Result, "pursue");

        // the loopback view carries the answer in the visit's result
        using HttpClient http = Http(daemon);
        using HttpRequestMessage show = Request(HttpMethod.Get, $"/flow-runs/{flowRunId}?project={Project}", daemon);
        using HttpResponseMessage shown = await http.SendAsync(show, None);
        using JsonDocument view = JsonDocument.Parse(await shown.Content.ReadAsStringAsync(None));
        Assert.Equal("pursue", view.RootElement.GetProperty("steps")[0].GetProperty("result").GetProperty("answer").GetString());

        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, null);
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");
        AssertResult(ended.Steps[1].Result, null);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_answer_of_stop_ends_the_flow_run()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Guid flowRunId = await FlowRunResumeAnswerApiTests.StartedAsync(daemon, client, PursueOrStop());
        await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, null, "stop");
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        Assert.Equal("decided", ended.Outcome);
        AssertResult(Assert.Single(ended.Steps).Result, "stop");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_resume_without_an_answer_pauses_again_on_the_same_visit_naming_the_answers_and_a_later_answer_routes()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Guid flowRunId = await FlowRunResumeAnswerApiTests.StartedAsync(daemon, client, PursueOrStop());
        FlowRunView first = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, "No answer yet.");
        FlowRunView again = await UntilViewAsync(
            client, flowRunId, view => view.Status == "paused" && view.UpdatedAt != first.UpdatedAt, "the step pauses again");

        Assert.Equal("Step 'ask' waits for a person's answer; resume with one of: 'pursue', 'stop'.", again.PauseReason);
        Assert.Equal("Answer ask.", again.Lever);
        FlowRunStepView visit = Assert.Single(again.Steps);
        Assert.Null(visit.RecordedAt);
        Assert.Null(visit.Result);

        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, null, "pursue");
        FlowRunView later = await UntilPausedAtAsync(client, flowRunId, "later");
        Assert.Equal(["ask", "later"], later.Steps.Select(s => s.Step));
        AssertResult(later.Steps[0].Result, "pursue");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_resume_without_an_answer_of_a_step_whose_verdict_transition_comes_first_ends_the_flow_run()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow flow = PursueOrStop() with
        {
            Steps = [Hold("ask", On("verdict", "done", Flow.End), On("answer", "approve", "later")), Hold("later", On("verdict", "done", Flow.End))],
        };
        Guid flowRunId = await FlowRunResumeAnswerApiTests.StartedAsync(daemon, client, flow);
        await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, null);
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        AssertResult(Assert.Single(ended.Steps).Result, null);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_answer_the_step_would_not_route_on_is_refused_and_the_flow_run_stays_paused_on_the_same_visit()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        using HttpClient http = Http(daemon);
        Guid flowRunId = await FlowRunResumeAnswerApiTests.StartedAsync(daemon, client, PursueOrStop());
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        await RefusedAsync(http, daemon, flowRunId, "maybe", "Step 'ask' takes no answer 'maybe': it matches none of its transitions on 'answer'.");

        FlowRunView unchanged = await FlowRunDriver.ShowAsync(client, Project, flowRunId);
        Assert.Equal(("paused", paused.UpdatedAt), (unchanged.Status, unchanged.UpdatedAt));
        Assert.Null(Assert.Single(unchanged.Steps).ResumeAnswer);

        await daemon.StopAsync(None);
    }

    public static TheoryData<bool> Dispatched => [false, true];

    [Theory]
    [MemberData(nameof(Dispatched))]
    public async Task A_restart_after_the_resume_with_an_answer_dispatches_the_step_again_with_that_answer(bool dispatchedSinceResume)
    {
        using var temp = new TemporaryHome();
        Guid flowRunId;
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, None))
        {
            await RegisteredProjects.AddAsync(first, Project, None);
            using var client = new DaemonClient(temp.Home);
            flowRunId = await FlowRunResumeAnswerApiTests.StartedAsync(first, client, PursueOrStop());
            await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");
            await first.StopAsync(None);
        }

        // The resume is recorded while no daemon runs, so nothing dispatches the step until the restart;
        // or a dispatch is recorded after it and the daemon stops before the step's result is.
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            var flowRuns = new FlowRunService(store, TimeProvider.System);
            FlowRun resumed = await flowRuns.ResumeAsync(Principal.LocalOwner, Project, flowRunId, null, "pursue", None);
            Assert.Equal("pursue", resumed.Current?.ResumeAnswer);
            if (dispatchedSinceResume)
            {
                await using IDocumentSession session = store.LightweightSession(Project);
                session.Events.Append(flowRunId, new SystemStepDispatched(flowRunId, "ask", CoreSystemSteps.WaitForHuman, Ids.New(), DateTimeOffset.UtcNow));
                await session.SaveChangesAsync(None);
            }
        }

        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, None);
        using var again = new DaemonClient(temp.Home);
        FlowRunView later = await UntilPausedAtAsync(again, flowRunId, "later");

        Assert.Equal(["ask", "later"], later.Steps.Select(s => s.Step));
        AssertResult(later.Steps[0].Result, "pursue");

        await second.StopAsync(None);
    }

    /// <summary>The result a wait-for-human step records: done, naming the step, with the answer when one was given and no answer field otherwise.</summary>
    private static void AssertResult(JsonElement? recorded, string? answer)
    {
        JsonElement result = Assert.NotNull(recorded);
        Assert.Equal("done", result.GetProperty("verdict").GetString());
        Assert.Equal(CoreSystemSteps.WaitForHuman, result.GetProperty("systemStep").GetString());
        if (answer is null)
        {
            Assert.False(result.TryGetProperty("answer", out _));
            Assert.Equal(2, result.EnumerateObject().Count());
        }
        else
        {
            Assert.Equal(JsonValueKind.String, result.GetProperty("answer").ValueKind);
            Assert.Equal(answer, result.GetProperty("answer").GetString());
            Assert.Equal(3, result.EnumerateObject().Count());
        }
    }

    private static Task<FlowRunView> UntilPausedAtAsync(DaemonClient client, Guid flowRunId, string step) =>
        UntilViewAsync(client, flowRunId, view => view.Status == "paused" && view.CurrentStep == step, $"the flow run pauses at '{step}'");

    private static async Task<FlowRunView> UntilViewAsync(DaemonClient client, Guid flowRunId, Func<FlowRunView, bool> holds, string what)
    {
        await FlowRunDriver.UntilAsync(async () => holds(await FlowRunDriver.ShowAsync(client, Project, flowRunId)), what);
        return await FlowRunDriver.ShowAsync(client, Project, flowRunId);
    }

    private static HttpClient Http(RunningDaemon daemon) => new() { BaseAddress = new Uri(daemon.Info.BaseAddress) };

    private static HttpRequestMessage Request(HttpMethod method, string path, RunningDaemon daemon)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(DaemonHost.TokenHeader, daemon.Info.Token);
        return request;
    }

    private static async Task RefusedAsync(HttpClient http, RunningDaemon daemon, Guid flowRunId, string answer, string rule)
    {
        using HttpRequestMessage request = Request(HttpMethod.Post, $"/flow-runs/{flowRunId}/resume", daemon);
        request.Content = JsonContent.Create(new FlowRunResumeRequest(Project, null, answer));

        using HttpResponseMessage response = await http.SendAsync(request, None);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        ErrorView? error = await response.Content.ReadFromJsonAsync<ErrorView>(None);
        Assert.NotNull(error);
        Assert.Equal(rule, error.Error);
    }
}
