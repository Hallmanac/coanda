using System.Text.Json;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Modules.Software.Repositories;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events;

namespace Coanda.Tests.Modules.Software.Steps;

/// <summary>
/// The task's repos field, which the software module owns: the first step that acts on a repo for a task
/// whose field is empty sets it to the project's one repo, a filled field is kept and names the repo every
/// later step acts on, a task whose type version declares no repos field acts on the project's only repo,
/// and a step pauses with a lever naming the field when the field cannot name one repo the project has.
/// </summary>
public sealed class RepoFieldTests : IAsyncLifetime
{
    private const string Project = SoftwareNode.Project;
    private const string Multi = "multi";
    private const string One = "https://github.com/example/one.git";
    private const string Two = "https://github.com/example/two.git";
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly ScriptedCommandRunner runner = new();
    private SoftwareNode node = null!;

    public async Task InitializeAsync() => node = await SoftwareNode.StartAsync(runner);

    public async Task DisposeAsync() => await node.DisposeAsync();

    [Fact]
    public async Task The_first_step_on_a_task_whose_field_is_empty_sets_it_to_the_projects_one_repo()
    {
        Parcel task = await node.TaskAsync("Fill my repos");
        Assert.Null(task.ValueOf(SoftwareSettings.ReposField));

        Done(await WorktreeAsync(task.Id));

        Parcel filled = await ShowAsync(Project, task.Id);
        Assert.Equal([RegisteredProjects.Repo], filled.ValueOf(SoftwareSettings.ReposField)?.Items);
        Assert.Contains($"remote add origin {RegisteredProjects.Remote}", runner.GitCalls);
        ParcelFieldSet set = Assert.Single(await FieldSetsAsync(Project, task.Id));
        Assert.Contains(SoftwareSteps.Module, set.SetBy, StringComparison.Ordinal);

        // A second step on the task finds the field filled and writes nothing.
        Done(await WorktreeAsync(task.Id));
        Assert.Single(await FieldSetsAsync(Project, task.Id));
    }

    [Fact]
    public async Task A_filled_field_is_kept_and_a_later_step_acts_on_the_repo_it_names()
    {
        await RegisterMultiAsync();
        Parcel task = await node.TaskAsync("Work in two", Multi);
        await node.Parcels.SetFieldAsync(Principal.LocalOwner, Multi, task.Id, SoftwareSettings.ReposField, FieldValue.OfItems("two"), None);

        Done(await WorktreeAsync(task.Id, Multi));

        Assert.Equal(["two"], (await ShowAsync(Multi, task.Id)).ValueOf(SoftwareSettings.ReposField)?.Items);
        Assert.Single(await FieldSetsAsync(Multi, task.Id));
        Assert.Contains($"remote add origin {Two}", runner.GitCalls);
        Assert.Contains("fetch --no-tags --quiet origin +refs/heads/trunk:refs/remotes/origin/trunk", runner.GitCalls);
        Assert.DoesNotContain(runner.GitCalls, c => c.Contains(One, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_task_whose_type_version_declares_no_repos_field_runs_against_the_projects_only_repo()
    {
        await Publishing.TypeAsync(node.Daemon, Project, SoftwareTypes.UnownedTask);
        Parcel task = await node.TaskAsync("From before the field");
        Assert.Equal(SoftwareTypes.UnownedTask.Hash, task.TypeHash);
        Assert.DoesNotContain(SoftwareTypes.UnownedTask.Fields, f => f.Name == SoftwareSettings.ReposField);

        Done(await WorktreeAsync(task.Id));

        Assert.Empty(await FieldSetsAsync(Project, task.Id));
        Assert.Contains($"remote add origin {RegisteredProjects.Remote}", runner.GitCalls);
    }

    [Fact]
    public async Task A_project_whose_record_holds_two_repos_pauses_a_task_with_an_empty_field_naming_the_field()
    {
        await RegisterMultiAsync();
        Parcel task = await node.TaskAsync("Which repo?", Multi);

        SystemStepAnswer.Paused paused = Paused(await WorktreeAsync(task.Id, Multi));

        Assert.Equal(
            $"Step 'worktree' has no repo to act on: project 'multi' has 2 repos ('one', 'two'), and the task's 'repos' field names none.",
            paused.Reason);
        Assert.Equal(
            $"Set the task's 'repos' field to the one repo it works in, which is one of 'one', 'two' (coa parcel set {Ids.Short(task.Id)} repos --item <repo> --project multi), then resume the flow run to perform step 'worktree' again.",
            paused.Lever);
        Assert.Empty(await FieldSetsAsync(Multi, task.Id));
        Assert.Empty(runner.Commands);
    }

    [Theory]
    [InlineData(new[] { "three" }, "has no repo to act on: the task's 'repos' field names 'three', and project 'multi' has no such repo; it has 'one', 'two'.")]
    [InlineData(new[] { "one", "two" }, "acts on one repo, and the task's 'repos' field names 2: 'one', 'two'.")]
    public async Task A_field_naming_a_repo_the_project_lacks_or_more_than_one_repo_pauses_naming_the_field(string[] repos, string what)
    {
        await RegisterMultiAsync();
        Parcel task = await node.TaskAsync("Misnamed", Multi);
        await node.Parcels.SetFieldAsync(Principal.LocalOwner, Multi, task.Id, SoftwareSettings.ReposField, FieldValue.OfItems(repos), None);

        foreach (string step in new[] { "worktree", "open-pr", "gated-merge", "rebase" })
        {
            (Guid flowRunId, Guid dispatchId) = await node.HeldAsync(task.Id, step, Multi);
            SystemStepAnswer.Paused paused = Paused(await node.DispatchAsync(SoftwareSteps.Canonical(step), task.Id, flowRunId, dispatchId, Multi));
            await node.StopAsync(flowRunId, Multi);

            Assert.Equal($"Step '{step}' {what}", paused.Reason);
            Assert.Contains("coa parcel set", paused.Lever, StringComparison.Ordinal);
        }

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task A_repo_with_no_primary_branch_pauses_naming_the_setting_and_the_command_that_sets_it()
    {
        await node.Projects.AddAsync(Principal.LocalOwner, "bare", RegisteredProjects.Remote, [], None);
        await Publishing.FlowAsync(node.Daemon, "bare", SoftwareNode.Steps);
        Parcel task = await node.TaskAsync("No branch", "bare");

        SystemStepAnswer.Paused paused = Paused(await WorktreeAsync(task.Id, "bare"));

        Assert.Equal("Step 'worktree' has no primary branch for repo 'repo': the software module's setting 'primary-branch' names none for it in project 'bare'.", paused.Reason);
        Assert.Equal(
            """Give the software module's setting 'primary-branch' a value in project 'bare' (coa project module set bare software primary-branch '{"repo":"<branch>"}'), then resume the flow run to perform step 'worktree' again.""",
            paused.Lever);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task The_worktree_gates_open_pr_and_rebase_steps_run_in_a_project_that_names_no_required_checks()
    {
        await node.Projects.AddAsync(Principal.LocalOwner, "unchecked", RegisteredProjects.Remote, RegisteredProjects.BranchOnly, None);
        await Publishing.FlowAsync(node.Daemon, "unchecked", SoftwareNode.Steps);
        Parcel task = await node.TaskAsync("Unchecked", "unchecked");

        foreach ((string step, string verdict) in new[] { ("worktree", "done"), ("gates", "done"), ("open-pr", "opened") })
        {
            (Guid flowRunId, Guid dispatchId) = await node.HeldAsync(task.Id, step, "unchecked");
            SystemStepAnswer answer = await node.DispatchAsync(SoftwareSteps.Canonical(step), task.Id, flowRunId, dispatchId, "unchecked");
            await node.StopAsync(flowRunId, "unchecked");

            Assert.Equal(verdict, Done(answer).GetProperty("verdict").GetString());
        }

        // The rebase reads the head an open-pr result recorded in its flow run's chain, recorded here by hand.
        FlowRun rebasing = await node.FlowRuns.StartAsync(Principal.LocalOwner, "unchecked", task.Id, SoftwareNode.Steps.Name, None);
        await node.FlowRuns.StartStepAsync(Principal.LocalOwner, "unchecked", rebasing.Id, "open-pr", None);
        JsonElement opened = JsonSerializer.SerializeToElement(new { verdict = "opened", systemStep = SoftwareSteps.OpenPr, number = 17, url = "https://github.com/example/repo/pull/17", head = ScriptedCommandRunner.PrimaryHead });
        await node.FlowRuns.RecordResultAsync(Principal.LocalOwner, "unchecked", rebasing.Id, "open-pr", SoftwareSteps.OpenPr, opened, None);
        await node.FlowRuns.StartStepAsync(Principal.LocalOwner, "unchecked", rebasing.Id, "rebase", None);
        string clone = node.Home.ProjectClonePath("unchecked");
        string branch = ProjectClones.BranchOf(task.Id);
        runner.AddWorktree(clone, node.Home.FlowRunWorkPath("unchecked", rebasing.Id), branch, ScriptedCommandRunner.PrimaryHead);
        runner.SetRemoteBranch(clone, branch, ScriptedCommandRunner.PrimaryHead);
        runner.SetTrackingRef(clone, ProjectClones.TrackingRefOf(branch), ScriptedCommandRunner.PrimaryHead);

        Assert.Equal("done", Done(await node.DispatchAsync(SoftwareSteps.Rebase, task.Id, rebasing.Id, project: "unchecked")).GetProperty("verdict").GetString());
    }

    /// <summary>Registers <see cref="Multi"/> with a record holding two repos, written straight to the store, as no command yet adds a second repo.</summary>
    private async Task RegisterMultiAsync()
    {
        var registered = new ProjectRegistered(Ids.New(), Multi, [new ProjectRepo("one", One), new ProjectRepo("two", Two)], Principal.LocalOwner.Id, DateTimeOffset.UtcNow);
        ProjectModulesSet on = ProjectDecider.SetModules(Multi, Coanda.Core.Projects.Project.Create(registered), [SoftwareSteps.Module], Principal.LocalOwner.Id, registered.RegisteredAt);
        await using (IDocumentSession session = node.Store.LightweightSession(Multi))
        {
            session.Events.StartStream<Coanda.Core.Projects.Project>(registered.ProjectId, registered, on);
            await session.SaveChangesAsync(None);
        }

        await node.SetAsync(Multi, SoftwareSettings.PrimaryBranch, new Dictionary<string, string> { ["one"] = "main", ["two"] = "trunk" });
        await node.SetAsync(Multi, SoftwareSettings.RequiredChecks, new[] { RegisteredProjects.RequiredCheck });
        await Publishing.FlowAsync(node.Daemon, Multi, SoftwareNode.Steps);
    }

    private async Task<SystemStepAnswer> WorktreeAsync(Guid parcelId, string project = Project)
    {
        (Guid flowRunId, Guid dispatchId) = await node.HeldAsync(parcelId, "worktree", project);
        SystemStepAnswer answer = await node.DispatchAsync(SoftwareSteps.Worktree, parcelId, flowRunId, dispatchId, project);
        await node.StopAsync(flowRunId, project);
        return answer;
    }

    private async Task<Parcel> ShowAsync(string project, Guid parcelId) =>
        await node.Parcels.ShowAsync(Principal.LocalOwner, project, parcelId, None) ?? throw new InvalidOperationException("No such parcel.");

    private async Task<IReadOnlyList<ParcelFieldSet>> FieldSetsAsync(string project, Guid parcelId)
    {
        await using IQuerySession session = node.Store.QuerySession(project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(parcelId, token: None);
        return [.. events.Select(e => e.Data).OfType<ParcelFieldSet>().Where(s => s.Field == SoftwareSettings.ReposField)];
    }

    private static JsonElement Done(SystemStepAnswer answer) => Assert.IsType<SystemStepAnswer.Done>(answer).Result;

    private static SystemStepAnswer.Paused Paused(SystemStepAnswer answer) => Assert.IsType<SystemStepAnswer.Paused>(answer);
}
