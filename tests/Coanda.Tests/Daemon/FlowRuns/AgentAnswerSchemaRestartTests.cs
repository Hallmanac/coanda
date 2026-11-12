using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.FlowRuns;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Store;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// A daemon restart that reattaches a live agent step checks its answer against the schema of the
/// agent whose content hash the launch recorded: an agent carrying its own content, or agent content
/// stored before agents carried any, which answers in the schema it names.
/// </summary>
public sealed class AgentAnswerSchemaRestartTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly string[] CleanPath =
    [
        "worktree", "build", "gates", "review-diff", "adversarial-review", "conformance-review", "open-pr", "move-to-review",
    ];

    /// <summary>A build agent whose schema answers "built" where the shipped one answers "done".</summary>
    private static readonly AgentDefinition Builder = SoftwareAgents.Build with
    {
        AnswerSchemaContent = JsonDocument.Parse(
            """{"type":"object","required":["verdict"],"properties":{"verdict":{"type":"string","enum":["built","blocked"]},"summary":{"type":"string"}}}""").RootElement.Clone(),
    };

    /// <summary>The build flow with its build step routing "built" where it routed "done".</summary>
    private static readonly Flow RoutesBuilt = SoftwareFlows.Build with
    {
        Steps =
        [
            .. SoftwareFlows.Build.Steps.Select(step => step.Name == "build"
                ? step with { Transitions = [.. step.Transitions.Select(t => t.Value.GetString() == "done" ? t with { Value = JsonSerializer.SerializeToElement("built") } : t)] }
                : step),
        ],
    };

    /// <summary>The software module's build agent as the store held it before agents carried schema content.</summary>
    private static readonly AgentDefinition StoredBuild = SoftwareAgents.Build with { AnswerSchemaContent = null };

    private const string StoredBuildHash = "37dd84328f49ca25";

    [Fact]
    public async Task A_reattached_agent_step_accepts_and_routes_a_verdict_only_its_agents_own_schema_allows()
    {
        using var temp = new TemporaryHome();
        var script = Scripts.CleanBuild();
        script["build"] = [Scripts.Answer("built", 1)];
        var stalled = new GatedExecutor(new ScriptedExecutor(script));
        (Guid flowRunId, SessionHandle issued) = await FlowRunRestartTests.StopWithSessionLeftAsync(
            temp, stalled, "Built", async first =>
            {
                DefinitionService definitions = first.Services.GetRequiredService<DefinitionService>();
                await PublishAsync(definitions, DefinitionKind.Agent, Builder.Name, AgentDefinition.CanonicalJson(Builder), Builder.Hash);
                await PublishAsync(definitions, DefinitionKind.Flow, RoutesBuilt.Name, Flow.CanonicalJson(RoutesBuilt), RoutesBuilt.Hash);
            });

        var held = new GatedExecutor(stalled.Inner);
        held.Open();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = held }, None);
        using var again = new DaemonClient(temp.Home);

        FlowRunView ended = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "ended");

        // "built" routed the build step to the gates, as the project's flow says
        Assert.Equal(CleanPath, ended.Steps.Select(s => s.Step));
        FlowRunStepView build = ended.Steps.Single(s => s.Step == "build");
        Assert.Equal(Builder.Hash, build.AgentHash);
        Assert.Equal("built", build.Answer?.GetProperty("verdict").GetString());
        // the answer was accepted on its first collect, with no retry
        Assert.DoesNotContain(held.Inner.Resumes, r => r.Handle == issued);
        Assert.Single(held.Inner.Spawns, s => s.Agent == "build");

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_reattached_agent_step_whose_launch_recorded_agent_content_stored_before_schema_content_answers_in_the_schema_it_names()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowRunId, SessionHandle issued) = await FlowRunRestartTests.StopWithSessionLeftAsync(
            temp, stalled, "Stored", first => StoreAsync(first.Services.GetRequiredService<IDocumentStore>(), StoredBuild));

        var held = new GatedExecutor(stalled.Inner);
        held.Open();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = held }, None);
        using var again = new DaemonClient(temp.Home);

        FlowRunView ended = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "ended");

        Assert.Equal(CleanPath, ended.Steps.Select(s => s.Step));
        FlowRunStepView build = ended.Steps.Single(s => s.Step == "build");
        // the launch recorded the stored content's hash, unchanged
        Assert.Equal(StoredBuildHash, build.AgentHash);
        Assert.Equal("done", build.Answer?.GetProperty("verdict").GetString());
        Assert.DoesNotContain(held.Inner.Resumes, r => r.Handle == issued);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_reattach_whose_recorded_agent_hash_resolves_to_no_agent_stops_the_session_and_pauses_naming_the_hash()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        Guid stored = Guid.Empty;
        (Guid flowRunId, SessionHandle issued) = await FlowRunRestartTests.StopWithSessionLeftAsync(
            temp, stalled, "Vanished", async first => stored = await StoreAsync(first.Services.GetRequiredService<IDocumentStore>(), StoredBuild));

        // The agent's document goes while no daemon runs, so no held definition carries the launch's hash.
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            await using IDocumentSession session = store.LightweightSession(Project);
            session.Delete<Definition>(stored);
            await session.SaveChangesAsync(None);
        }

        var held = new GatedExecutor(stalled.Inner);
        held.Open();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = held }, None);
        using var again = new DaemonClient(temp.Home);

        FlowRunView paused = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "paused");

        Assert.Equal(
            $"Step 'build' reattached the session agent 'build' was launched in across a daemon restart (session {Ids.Short(issued.Id)}), but the agent hash {StoredBuildHash} its launch recorded resolves to no agent definition, so its answer could not be checked against the agent's answer schema and the session was stopped.",
            paused.PauseReason);
        Assert.Equal(FlowRunLevers.PerformAgain("build"), paused.Lever);
        Assert.Contains(issued, held.Inner.Stops);
        // nothing was collected from the session
        Assert.False(paused.Steps.Single(s => s.Step == "build").Answer.HasValue);

        await second.StopAsync(None);
    }

    private static async Task PublishAsync(DefinitionService definitions, DefinitionKind kind, string name, string content, string hash)
    {
        Definition draft = await definitions.DraftAsync(Principal.LocalOwner, kind, DefinitionScope.Project, Project, name, content, hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
    }

    /// <summary>
    /// Records an agent as published in the project's scope directly, as the store fixtures hold agents
    /// stored before agents carried schema content, which a draft now refuses.
    /// </summary>
    private static async Task<Guid> StoreAsync(IDocumentStore store, AgentDefinition agent)
    {
        Guid id = Ids.New();
        DateTimeOffset at = DateTimeOffset.UnixEpoch;
        var drafted = new DefinitionDrafted(
            id, DefinitionKind.Agent, agent.Name, DefinitionScope.Project, Project, 1, AgentDefinition.CanonicalJson(agent), agent.Hash, null, "owner", at);
        await using IDocumentSession session = store.LightweightSession(Project);
        session.Events.StartStream<Definition>(id, drafted, new DefinitionPublished(id, "owner", at));
        await session.SaveChangesAsync(None);
        return id;
    }
}
