using System.Text.Json;
using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Tests.Core;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.SystemSteps;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// The shipped discovery flow on the scripted executor, for an idea created on this build and moved into its
/// discovery stage: a <c>derive</c> answer listing tasks pauses the flow run at its gate, where an answer of
/// <c>pursue</c> ends it with the outcome <c>parcels-derived</c> and one task parcel per entry, each derived
/// from the idea by that flow run, and an answer of <c>stop</c> ends it with the outcome <c>stopped</c> and
/// none; a <c>derive</c> answer with no list pauses at derive-parcels once pursued, and a
/// <c>carry-forward</c> answer ends the flow run with the outcome <c>carried-forward</c>, no gate, and no parcel.
/// </summary>
public sealed class DiscoveryFlowRunTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private static ScriptedExecutor Discovering(string answer) => new(new Dictionary<string, IReadOnlyList<ScriptEntry>>
    {
        ["discovery"] = [new ScriptEntry(answer, "/transcripts/1.jsonl", Scripts.Usage(1))],
    });

    /// <summary>Starts a daemon whose discovery agent answers <paramref name="answer"/>, and moves a new idea into discovery.</summary>
    private static async Task<(RunningDaemon Daemon, DaemonClient Client, Parcel Idea, Guid FlowRunId)> DiscoveringAsync(TemporaryHome temp, string answer)
    {
        RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = Discovering(answer) }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        var client = new DaemonClient(temp.Home);
        ParcelView idea = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "idea", "Split the work", null), None);
        await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, idea.Id, "discovery"), None);
        FlowRunView flowRun = await FlowRunDriver.BoundAsync(client, Project, idea.Id);
        Assert.Equal(("discovery", SoftwareFlows.Discovery.Hash), (flowRun.FlowName, flowRun.FlowHash));

        Parcel created = await daemon.Services.GetRequiredService<ParcelService>().ShowAsync(Principal.LocalOwner, Project, idea.Id, None)
            ?? throw new InvalidOperationException("No idea.");
        // created under the idea type this build ships, which may derive tasks
        Assert.Equal(SoftwareTypes.Idea.Hash, created.TypeHash);
        return (daemon, client, created, flowRun.Id);
    }

    private const string TwoTasks =
        """{"verdict":"derive","summary":"Two bounded tasks.","parcels":[{"type":"task","title":"Write the parser"},{"type":"task","title":"Wire the parser in"}]}""";

    /// <summary>
    /// Waits for the flow run to pause at the gate, and holds it to the gate's lever on the loopback view and in
    /// what <c>coa flow-run show</c> prints, with no parcel but the idea created yet.
    /// </summary>
    private static async Task AtGateAsync(RunningDaemon daemon, DaemonClient client, Parcel idea, Guid flowRunId)
    {
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        Assert.Equal("gate", paused.CurrentStep);
        Assert.Equal(["discover", "gate"], paused.Steps.Select(s => s.Step));
        Assert.Equal("Step 'gate' waits for a person.", paused.PauseReason);
        Assert.Equal(SoftwareFlows.DiscoveryGateLever, paused.Lever);
        IReadOnlyList<string> shown = FlowRunRendering.Show(paused);
        Assert.Contains($"{"Lever",-8}{SoftwareFlows.DiscoveryGateLever}", shown);
        Assert.Contains(shown, line => line.Contains("verdict=derive", StringComparison.Ordinal) && line.Contains("summary=Two bounded tasks.", StringComparison.Ordinal));
        // the tasks a pursue would derive, which the lever sends the person to read here first
        int discover = shown.ToList().FindIndex(line => line.StartsWith("  discover  ", StringComparison.Ordinal));
        Assert.Equal(
            ["    parcels[1]  type=task  title=Write the parser", "    parcels[2]  type=task  title=Wire the parser in"],
            shown.Skip(discover + 1).Take(2));

        Parcel only = Assert.Single(await daemon.Services.GetRequiredService<ParcelService>().ListAsync(Principal.LocalOwner, Project, None));
        Assert.Equal(idea.Id, only.Id);
    }

    /// <summary>
    /// Runs a discovery flow run to its gate on a daemon of its own, answers it <paramref name="answer"/>, and
    /// returns the ended flow run's view and the idea it ran for.
    /// </summary>
    private static async Task<(FlowRunView Ended, Parcel Idea)> EndedAtGateAsync(string answer)
    {
        using var temp = new TemporaryHome();
        (RunningDaemon daemon, DaemonClient client, Parcel idea, Guid flowRunId) = await DiscoveringAsync(temp, TwoTasks);
        await using (daemon)
        using (client)
        {
            await AtGateAsync(daemon, client, idea, flowRunId);
            await FlowRunDriver.ResumeAsync(client, Project, flowRunId, null, answer);
            FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");
            await daemon.StopAsync(None);
            return (ended, idea);
        }
    }

    /// <summary>The flow-run-ended event the trigger loop builds for <paramref name="ended"/>, from the outcome it recorded.</summary>
    private static TriggerEvent EndedEvent(FlowRunView ended, Parcel idea) => TriggerEvent.FlowRunEnded(
        ended.FlowName,
        ended.Outcome ?? throw new InvalidOperationException("The ended flow run recorded no outcome."),
        idea.TypeName,
        idea.Title);

    private static void AssertAnswered(FlowRunStepView gate, string answer)
    {
        Assert.Equal("gate", gate.Step);
        JsonElement result = gate.Result ?? throw new InvalidOperationException("The gate recorded no result.");
        Assert.Equal(answer, result.GetProperty(WaitForHumanAnswer.Field).GetString());
    }

    [Fact]
    public async Task A_derive_answer_listing_two_tasks_waits_at_the_gate_and_an_answer_of_pursue_ends_the_flow_run_with_parcels_derived_and_two_task_parcels_derived_from_the_idea()
    {
        using var temp = new TemporaryHome();
        (RunningDaemon daemon, DaemonClient client, Parcel idea, Guid flowRunId) = await DiscoveringAsync(temp, TwoTasks);
        await using (daemon)
        using (client)
        {
            await AtGateAsync(daemon, client, idea, flowRunId);

            await FlowRunDriver.ResumeAsync(client, Project, flowRunId, null, "pursue");
            FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

            Assert.Equal("parcels-derived", ended.Outcome);
            Assert.Equal(["discover", "gate", "derive-parcels"], ended.Steps.Select(s => s.Step));
            AssertAnswered(ended.Steps[1], "pursue");
            JsonElement result = ended.Steps[^1].Result ?? throw new InvalidOperationException("The derive-parcels step recorded no result.");
            Assert.Equal(SystemStepAnswer.DoneVerdict, result.GetProperty(SystemStepAnswer.Verdict).GetString());
            List<Guid> ids = [.. result.GetProperty(DeriveParcelsSystemStep.ParcelIdsMember).EnumerateArray().Select(id => Guid.Parse(id.GetString() ?? string.Empty))];

            ParcelService parcels = daemon.Services.GetRequiredService<ParcelService>();
            List<Parcel> tasks = [.. (await parcels.ListAsync(Principal.LocalOwner, Project, None)).Where(p => p.Id != idea.Id)];
            Assert.Equal(2, tasks.Count);
            Assert.Equal(ids.Order(), tasks.Select(p => p.Id).Order());

            List<Parcel> listed = [.. ids.Select(id => tasks.Single(p => p.Id == id))];
            Assert.Equal(["Write the parser", "Wire the parser in"], listed.Select(p => p.Title));
            Assert.All(listed, task =>
            {
                Assert.Equal((SoftwareTypes.Task.Name, SoftwareTypes.Task.Hash, SoftwareTypes.Task.EntryStages[0]), (task.TypeName, task.TypeHash, task.Stage));
                ParcelRelation relation = Assert.Single(task.Relations);
                Assert.Equal((RelationKinds.DerivedFrom, idea.Id, (Guid?)flowRunId), (relation.Kind, relation.RelatedParcelId, relation.FlowRunId));
            });

            await daemon.StopAsync(None);
        }
    }

    [Fact]
    public async Task A_derive_answer_listing_two_tasks_waits_at_the_gate_and_an_answer_of_stop_ends_the_flow_run_with_stopped_and_nothing_derived()
    {
        using var temp = new TemporaryHome();
        (RunningDaemon daemon, DaemonClient client, Parcel idea, Guid flowRunId) = await DiscoveringAsync(temp, TwoTasks);
        await using (daemon)
        using (client)
        {
            await AtGateAsync(daemon, client, idea, flowRunId);

            await FlowRunDriver.ResumeAsync(client, Project, flowRunId, null, "stop");
            FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

            Assert.Equal("stopped", ended.Outcome);
            Assert.Contains($"{"Outcome",-8}stopped", FlowRunRendering.Show(ended));
            Assert.Equal(["discover", "gate"], ended.Steps.Select(s => s.Step));
            AssertAnswered(ended.Steps[1], "stop");
            Parcel only = Assert.Single(await daemon.Services.GetRequiredService<ParcelService>().ListAsync(Principal.LocalOwner, Project, None));
            Assert.Equal(idea.Id, only.Id);

            await daemon.StopAsync(None);
        }
    }

    [Fact]
    public async Task A_derive_answer_with_no_parcels_list_passes_the_answer_schema_and_once_pursued_pauses_at_derive_parcels_creating_nothing()
    {
        using var temp = new TemporaryHome();
        (RunningDaemon daemon, DaemonClient client, Parcel idea, Guid flowRunId) = await DiscoveringAsync(
            temp, """{"verdict":"derive","summary":"Two tasks, described here."}""");
        await using (daemon)
        using (client)
        {
            FlowRunView atGate = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");
            Assert.Equal("gate", atGate.CurrentStep);

            await FlowRunDriver.ResumeAsync(client, Project, flowRunId, null, "pursue");
            FlowRunView? paused = null;
            await FlowRunDriver.UntilAsync(
                async () =>
                {
                    paused = await FlowRunDriver.ShowAsync(client, Project, flowRunId);
                    return paused is { Status: "paused", CurrentStep: "derive-parcels" };
                },
                "the flow run pauses at derive-parcels");

            Assert.NotNull(paused);
            Assert.Equal("Step 'derive-parcels' could not derive parcels: the answer of step 'discover' has no 'parcels' array.", paused.PauseReason);
            Parcel only = Assert.Single(await daemon.Services.GetRequiredService<ParcelService>().ListAsync(Principal.LocalOwner, Project, None));
            Assert.Equal(idea.Id, only.Id);

            await daemon.StopAsync(None);
        }
    }

    [Fact]
    public async Task A_carry_forward_answer_ends_the_flow_run_with_carried_forward_without_visiting_derive_parcels_or_creating_a_parcel()
    {
        using var temp = new TemporaryHome();
        (RunningDaemon daemon, DaemonClient client, Parcel idea, Guid flowRunId) = await DiscoveringAsync(
            temp, """{"verdict":"carry-forward","summary":"The storage shape is still unknown."}""");
        await using (daemon)
        using (client)
        {
            FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

            Assert.Equal("carried-forward", ended.Outcome);
            Assert.Contains($"{"Outcome",-8}carried-forward", FlowRunRendering.Show(ended));
            Assert.Equal(["discover"], ended.Steps.Select(s => s.Step));
            Parcel only = Assert.Single(await daemon.Services.GetRequiredService<ParcelService>().ListAsync(Principal.LocalOwner, Project, None));
            Assert.Equal(idea.Id, only.Id);

            await daemon.StopAsync(None);
        }
    }

    [Fact]
    public async Task A_trigger_on_the_discovery_flows_end_filtered_on_stopped_matches_the_stopped_flow_runs_end_and_not_the_pursued_one()
    {
        TriggerDefinition onStop = SampleTriggers.EvaluateBuild with
        {
            Name = "on-discovery-stopped",
            Filters = SampleTriggers.Filters((TriggerEvent.FlowField, SoftwareFlows.Discovery.Name), (TriggerEvent.OutcomeField, "stopped")),
        };
        Trigger matcher = Trigger.ForDefinition(onStop);

        (FlowRunView stopped, Parcel stoppedIdea) = await EndedAtGateAsync("stop");
        (FlowRunView pursued, Parcel pursuedIdea) = await EndedAtGateAsync("pursue");

        Assert.True(matcher.Matches(EndedEvent(stopped, stoppedIdea)));
        Assert.False(matcher.Matches(EndedEvent(pursued, pursuedIdea)));
    }
}
