using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>A person's answer on a resume through the loopback resume route, and the visit's copy the flow run's view shows.</summary>
public sealed class FlowRunResumeAnswerApiTests
{
    internal const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_resume_with_an_answer_shows_it_on_the_visit_it_landed_on_after_that_visit_records_and_on_no_other()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        using HttpClient http = Http(daemon);
        Guid flowRunId = await StartedAsync(daemon, client, TwoHolds());
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        await RefusedAsync(http, daemon, flowRunId, "yes\nno", "A resume's answer is a single line; it holds no carriage return, line feed, or other control character.");
        await RefusedAsync(http, daemon, flowRunId, "  ", "A resume's answer is blank; give an answer that is not blank, or resume without one.");
        await RefusedAsync(http, daemon, flowRunId, new string('a', 201), "A resume's answer is at most 200 characters after trimming; this one is 201.");
        FlowRunView unchanged = await FlowRunDriver.ShowAsync(client, Project, flowRunId);
        // a refused resume records nothing
        Assert.Equal(("paused", paused.UpdatedAt), (unchanged.Status, unchanged.UpdatedAt));
        Assert.Null(Assert.Single(unchanged.Steps).ResumeAnswer);

        FlowRunView resumed = await FlowRunDriver.ResumeAsync(client, Project, flowRunId, "Looked it over.", "  approve  ");
        Assert.Equal("approve", resumed.Steps[0].ResumeAnswer);

        // The first hold records done on the resume and the flow run pauses again at the second.
        FlowRunView second = await UntilPausedAtAsync(client, flowRunId, "confirm");
        Assert.Equal(["approve", null], second.Steps.Select(s => s.ResumeAnswer));
        Assert.NotNull(second.Steps[0].RecordedAt);
        Assert.All(second.Steps, s => Assert.Null(s.Answer));

        // The view carries it as resumeAnswer.
        using HttpRequestMessage show = Get($"/flow-runs/{flowRunId}?project={Project}", daemon);
        using HttpResponseMessage shown = await http.SendAsync(show, None);
        using JsonDocument view = JsonDocument.Parse(await shown.Content.ReadAsStringAsync(None));
        Assert.Equal("approve", view.RootElement.GetProperty("steps")[0].GetProperty("resumeAnswer").GetString());

        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, null);
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");
        Assert.Equal(["approve", null], ended.Steps.Select(s => s.ResumeAnswer));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_resume_with_an_answer_of_a_recorded_visit_is_refused_and_one_without_resumes_and_leaves_its_answer()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        using HttpClient http = Http(daemon);
        Guid flowRunId = await StartedAsync(daemon, client, PausingHold());
        await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        // The hold records done on the resume, and its transition pauses the flow run with that visit recorded.
        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, null, "approve");
        FlowRunView capped = await UntilViewAsync(
            client, flowRunId, view => view is { Status: "paused", Steps: [{ RecordedAt: not null }] }, "the recorded hold pauses on its answer");
        Assert.Equal("approve", capped.Steps[0].ResumeAnswer);

        await RefusedAsync(
            http, daemon, flowRunId, "reject",
            $"Flow run {Ids.Short(flowRunId)}'s visit of step 'approve' has recorded, so the resume performs it again as a new visit and an answer would reach no step; resume without one.");
        FlowRunView unchanged = await FlowRunDriver.ShowAsync(client, Project, flowRunId);
        Assert.Equal(capped.UpdatedAt, unchanged.UpdatedAt);

        FlowRunView resumed = await FlowRunDriver.ResumeAsync(client, Project, flowRunId, "Go on.");
        Assert.Equal("running", resumed.Status);
        // the recorded visit keeps its answer, and the visit performed again holds none
        FlowRunView again = await UntilViewAsync(
            client, flowRunId, view => view is { Status: "paused", Steps.Count: 2 }, "the hold performed again pauses");
        Assert.Equal(["approve", null], again.Steps.Select(s => s.ResumeAnswer));

        await daemon.StopAsync(None);
    }

    /// <summary>A wait-for-human step going to <paramref name="target"/>, on the answer <paramref name="answer"/> when one is named and otherwise on done.</summary>
    private static FlowStep Hold(string name, string target, string? answer = null) => new()
    {
        Name = name,
        SystemStep = CoreSystemSteps.WaitForHuman,
        Arguments = new Dictionary<string, JsonElement> { ["lever"] = FlowValue.Of("Resume with an answer.") },
        Transitions = answer is null
            ? [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 2, Target = target }]
            : [new FlowTransition { Field = "answer", Value = FlowValue.Of(answer), VisitCap = 2, Target = target }],
    };

    internal static Flow TwoHolds() => new()
    {
        Name = "two-holds",
        Accepts = ["task"],
        Outcome = "approved",
        Budget = 1m,
        SpawnCap = 1,
        Steps = [Hold("approve", "confirm", "approve"), Hold("confirm", Flow.End)],
    };

    private static Flow PausingHold() => new()
    {
        Name = "pausing-hold",
        Accepts = ["task"],
        Outcome = "approved",
        Budget = 1m,
        SpawnCap = 1,
        Steps = [Hold("approve", Flow.Pause, "approve")],
    };

    internal static async Task<Guid> StartedAsync(RunningDaemon daemon, DaemonClient client, Flow flow)
    {
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, Project, flow.Name, Flow.CanonicalJson(flow), flow.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Needs an answer", null), None);
        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, flow.Name);
        return started.Id;
    }

    private static Task<FlowRunView> UntilPausedAtAsync(DaemonClient client, Guid flowRunId, string step) =>
        UntilViewAsync(client, flowRunId, view => view.Status == "paused" && view.CurrentStep == step, $"the flow run pauses at '{step}'");

    /// <summary>Reads the flow run's view until <paramref name="holds"/> holds for it, and gives that view.</summary>
    private static async Task<FlowRunView> UntilViewAsync(DaemonClient client, Guid flowRunId, Func<FlowRunView, bool> holds, string what)
    {
        await FlowRunDriver.UntilAsync(async () => holds(await FlowRunDriver.ShowAsync(client, Project, flowRunId)), what);
        return await FlowRunDriver.ShowAsync(client, Project, flowRunId);
    }

    private static HttpClient Http(RunningDaemon daemon) => new() { BaseAddress = new Uri(daemon.Info.BaseAddress) };

    private static HttpRequestMessage Get(string path, RunningDaemon daemon)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(DaemonHost.TokenHeader, daemon.Info.Token);
        return request;
    }

    private static async Task RefusedAsync(HttpClient http, RunningDaemon daemon, Guid flowRunId, string answer, string rule)
    {
        var body = new FlowRunResumeRequest(Project, null, answer);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/flow-runs/{flowRunId}/resume") { Content = JsonContent.Create(body) };
        request.Headers.Add(DaemonHost.TokenHeader, daemon.Info.Token);

        using HttpResponseMessage response = await http.SendAsync(request, None);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        ErrorView? error = await response.Content.ReadFromJsonAsync<ErrorView>(None);
        Assert.NotNull(error);
        Assert.Equal(rule, error.Error);
    }
}
