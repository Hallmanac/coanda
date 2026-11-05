using Coanda.Cli;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Projects;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

public sealed class CredentialProfileFlowRunTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly CredentialProfile Work = new(SoftwareAgents.Vendor, "work", "/home/me/.claude-work", null);
    private static readonly CredentialProfile Ci = new(SoftwareAgents.Vendor, "ci", null, "op read op://ci/key");
    private static readonly CredentialProfile OtherVendorWork = new("other-vendor", "work", "/home/me/.other", null);

    /// <summary>One agent step by the shipped build agent, which ends the flow run; it may spawn twice.</summary>
    private static readonly Flow OneAgentStep = new()
    {
        Name = "one-agent-step",
        Accepts = ["task"],
        Outcome = "built",
        Budget = 10m,
        SpawnCap = 2,
        Steps =
        [
            new FlowStep
            {
                Name = "build",
                Agent = SoftwareAgents.Build.Name,
                AnswerSchema = "build",
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    private static ScriptedExecutor Builds(int answers) => new(new Dictionary<string, IReadOnlyList<ScriptEntry>>
    {
        [SoftwareAgents.Build.Name] = [.. Enumerable.Range(1, answers).Select(n => Scripts.Answer("done", n))],
    });

    [Fact]
    public async Task An_agent_step_in_a_project_that_names_a_profile_spawns_with_that_profile_under_the_agents_vendor()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Builds(1);
        var options = new DaemonOptions { Executor = executor, Profiles = [OtherVendorWork, Ci, Work] };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, options, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await Publishing.FlowAsync(daemon, Project, OneAgentStep);
        using var client = new DaemonClient(temp.Home);
        await client.PostAsync<ProjectView>("/projects/profile", new ProjectSetProfileRequest(Project, "work"), None);

        FlowRunView ended = await RunAsync(client, "With a profile", "ended");

        Assert.Equal("built", ended.Outcome);
        Assert.Equal(Work, Assert.Single(executor.Spawns).CredentialProfile);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_agent_step_in_a_project_that_names_no_profile_spawns_with_none()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Builds(1);
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor, Profiles = [Work] }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await Publishing.FlowAsync(daemon, Project, OneAgentStep);
        using var client = new DaemonClient(temp.Home);

        FlowRunView ended = await RunAsync(client, "No profile", "ended");

        Assert.Equal("built", ended.Outcome);
        Assert.Null(Assert.Single(executor.Spawns).CredentialProfile);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_profile_the_configuration_no_longer_holds_pauses_before_the_launch_and_a_resume_after_set_profile_spawns_with_the_held_one()
    {
        using var temp = new TemporaryHome();
        await using (RunningDaemon earlier = await Daemons.StartAsync(temp.Home, new DaemonOptions { Profiles = [Work] }, None))
        {
            await RegisteredProjects.AddAsync(earlier, Project, None);
            await earlier.Services.GetRequiredService<ProjectService>().SetProfileAsync(Principal.LocalOwner, Project, "work", None);
            await earlier.StopAsync(None);
        }

        // The node's configuration changed between starts: it holds 'work' only under another vendor now.
        ScriptedExecutor executor = Builds(2);
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor, Profiles = [OtherVendorWork, Ci] }, None);
        await Publishing.FlowAsync(daemon, Project, OneAgentStep);
        using var client = new DaemonClient(temp.Home);

        FlowRunView paused = await RunAsync(client, "Missing profile", "paused");

        Assert.Equal($"Project '{Project}' names the credential profile 'work', which the node configuration loaded at start holds no entry for under the vendor '{SoftwareAgents.Vendor}' of agent '{SoftwareAgents.Build.Name}'.", paused.PauseReason);
        Assert.Equal(FlowRunLevers.PerformAgain("build"), paused.Lever);
        // the spawn the visit counted is left unused, since the launch was never recorded
        Assert.Equal(1, paused.Spawns);
        // nothing is spawned under a profile the node does not hold
        Assert.Empty(executor.Spawns);

        await client.PostAsync<ProjectView>("/projects/profile", new ProjectSetProfileRequest(Project, "ci"), None);
        await daemon.Services.GetRequiredService<FlowRunService>().ResumeAsync(Principal.LocalOwner, Project, paused.Id, "Named a held profile.", None);
        FlowRunView resumed = await FlowRunDriver.UntilAsync(client, Project, paused.Id, "ended");

        Assert.Equal(Ci, Assert.Single(executor.Spawns).CredentialProfile);
        FlowRunView heldFromStart = await RunAsync(client, "Profile held from the start", "ended");
        Assert.Equal(2, executor.Spawns.Count);
        Assert.All(executor.Spawns, s => Assert.True(s.CredentialProfile == Ci));
        // the pause used none of the flow's spawns
        Assert.Equal(heldFromStart.Spawns, resumed.Spawns);
        Assert.Equal(1, resumed.Spawns);

        await daemon.StopAsync(None);
    }

    private static async Task<FlowRunView> RunAsync(DaemonClient client, string title, string status)
    {
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", title, null), None);
        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, OneAgentStep.Name);
        return await FlowRunDriver.UntilAsync(client, Project, started.Id, status);
    }
}
