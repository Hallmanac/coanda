using System.Text.Json;
using Coanda.Cli;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.SystemSteps;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// The shipped discovery flow on the scripted executor, for an idea created on this build and moved into its
/// discovery stage: a <c>derive</c> answer listing tasks ends the flow run with one task parcel per entry, each
/// derived from the idea by that flow run, and a <c>carry-forward</c> answer ends it with none.
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

    [Fact]
    public async Task A_derive_answer_listing_two_tasks_ends_the_flow_run_with_two_task_parcels_derived_from_the_idea()
    {
        using var temp = new TemporaryHome();
        (RunningDaemon daemon, DaemonClient client, Parcel idea, Guid flowRunId) = await DiscoveringAsync(
            temp,
            """{"verdict":"derive","summary":"Two bounded tasks.","parcels":[{"type":"task","title":"Write the parser"},{"type":"task","title":"Wire the parser in"}]}""");
        await using (daemon)
        using (client)
        {
            FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

            Assert.Equal("parcels-derived", ended.Outcome);
            Assert.Equal(["discover", "derive-parcels"], ended.Steps.Select(s => s.Step));
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
    public async Task A_carry_forward_answer_ends_the_flow_run_without_visiting_derive_parcels_or_creating_a_parcel()
    {
        using var temp = new TemporaryHome();
        (RunningDaemon daemon, DaemonClient client, Parcel idea, Guid flowRunId) = await DiscoveringAsync(
            temp, """{"verdict":"carry-forward","summary":"The storage shape is still unknown."}""");
        await using (daemon)
        using (client)
        {
            FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

            Assert.Equal(["discover"], ended.Steps.Select(s => s.Step));
            Parcel only = Assert.Single(await daemon.Services.GetRequiredService<ParcelService>().ListAsync(Principal.LocalOwner, Project, None));
            Assert.Equal(idea.Id, only.Id);

            await daemon.StopAsync(None);
        }
    }
}
