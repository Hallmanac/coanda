using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Projects;
using Coanda.Tests.Daemon.FlowRuns;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// A module's directory may carry a skills folder beside its definitions folder, which the daemon reads
/// at start; an agent step whose agent names skills gives its session each one, resolved from the
/// modules switched on for the parcel's project, or pauses before its launch is recorded.
/// </summary>
public sealed class ModuleSkillTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The shipped build agent under another name, naming the skills it is given.</summary>
    private static AgentDefinition Writer(params string[] skills) => SoftwareAgents.Build with { Name = "writer", Skills = skills };

    /// <summary>One agent step by the writer, which ends the flow run.</summary>
    private static readonly Flow OneAgentStep = new()
    {
        Name = "write",
        Accepts = ["task"],
        Outcome = "written",
        Budget = 10m,
        SpawnCap = 2,
        Steps =
        [
            new FlowStep
            {
                Name = "write",
                Agent = "writer",
                AnswerSchema = "build",
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    [Fact]
    public async Task A_skills_folder_is_read_with_each_skill_held_by_name_and_its_dot_entries_passed_over()
    {
        using StandInModuleDirectory docs = StandInModuleDirectory.Write("docs", "1.0.0");
        string greet = docs.WriteSkill("greet", "Say hello.", ("scripts/run.sh", "echo hi"), (".hidden", "kept with the skill"));
        string check = docs.WriteSkill("check_2-a", "Check it.");
        File.WriteAllText(Path.Combine(docs.SkillsPath, ".DS_Store"), "not a skill");
        Directory.CreateDirectory(Path.Combine(docs.SkillsPath, ".git"));

        ModuleArtifact artifact = await ModuleArtifact.ReadAsync(docs.Path, None);

        Assert.Equal("docs", artifact.Module);
        Assert.Equal([new ModuleSkill("check_2-a", check), new ModuleSkill("greet", greet)], artifact.Skills);
    }

    [Fact]
    public async Task A_module_with_no_skills_folder_reads_as_it_did_with_no_skills()
    {
        using StandInModuleDirectory docs = StandInModuleDirectory.Write("docs", "1.0.0");

        ModuleArtifact artifact = await ModuleArtifact.ReadAsync(docs.Path, None);

        Assert.Empty(artifact.Skills);
        Assert.Empty(artifact.Definitions);
    }

    /// <summary>A skills folder the start refuses: how it is laid out, the path the refusal names relative to the module's directory, and its wording.</summary>
    public static TheoryData<string, string, string> BrokenSkills() => new()
    {
        { "loose", "skills/notes.md", "is a file directly in a skills folder" },
        { "unnamed", "skills/empty", "is a skill folder that holds no SKILL.md" },
        { "lowercase", "skills/lower", "is a skill folder that holds no SKILL.md" },
        { "dotted", "skills/greet.v2", "is a skill folder whose name is not a plain name (ASCII letters, digits, hyphens, and underscores)" },
        { "spaced", "skills/two words", "is a skill folder whose name is not a plain name" },
        { "unicode", "skills/grüß", "is a skill folder whose name is not a plain name" },
    };

    [Theory]
    [MemberData(nameof(BrokenSkills))]
    public async Task A_start_is_refused_naming_the_path_when_a_skills_folder_breaks_its_layout(string layout, string named, string rule)
    {
        using var temp = new TemporaryHome();
        using StandInModuleDirectory docs = StandInModuleDirectory.Write("docs", "1.0.0");
        docs.WriteSkill("greet", "Say hello.");
        switch (layout)
        {
            case "loose":
                File.WriteAllText(Path.Combine(docs.SkillsPath, "notes.md"), "a loose file");
                break;
            case "unnamed":
                Directory.CreateDirectory(Path.Combine(docs.SkillsPath, "empty", "nested"));
                File.WriteAllText(Path.Combine(docs.SkillsPath, "empty", "nested", "SKILL.md"), "too deep");
                break;
            case "lowercase":
                Directory.CreateDirectory(Path.Combine(docs.SkillsPath, "lower"));
                File.WriteAllText(Path.Combine(docs.SkillsPath, "lower", "skill.md"), "not named SKILL.md");
                break;
            default:
                docs.WriteSkill(Path.GetFileName(named), "A skill.");
                break;
        }

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => StartAndStopAsync(temp.Home, new DaemonOptions { ModulePaths = [docs.Path] }));

        Assert.Contains($"'{Path.Combine([docs.Path, .. named.Split('/')])}' {rule}", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(temp.Home.DaemonInfoPath));
    }

    [Theory]
    [InlineData("folder")]
    [InlineData("skill")]
    [InlineData("file")]
    [InlineData("dotted")]
    public async Task A_start_is_refused_naming_the_link_when_a_skills_folder_is_or_holds_a_symbolic_link(string where)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a link there needs a privilege the test runner may not hold.
        }

        using var temp = new TemporaryHome();
        using StandInModuleDirectory docs = StandInModuleDirectory.Write("docs", "1.0.0");
        string outside = Path.Combine(temp.Home.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "SKILL.md"), "from outside");
        string link;
        if (where == "folder")
        {
            link = docs.SkillsPath;
            Directory.CreateSymbolicLink(link, outside);
        }
        else
        {
            string greet = docs.WriteSkill("greet", "Say hello.");
            link = where switch
            {
                "skill" => Path.Combine(docs.SkillsPath, "borrowed"),
                "file" => Path.Combine(greet, "deep", "notes.md"),
                _ => Path.Combine(docs.SkillsPath, ".cache"),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(link) ?? docs.SkillsPath);
            if (where == "file")
            {
                File.CreateSymbolicLink(link, Path.Combine(outside, "SKILL.md"));
            }
            else
            {
                Directory.CreateSymbolicLink(link, outside);
            }
        }

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => StartAndStopAsync(temp.Home, new DaemonOptions { ModulePaths = [docs.Path] }));

        Assert.Contains($"'{link}' is a symbolic link or other reparse point", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(temp.Home.DaemonInfoPath));
    }

    [Fact]
    public async Task An_agent_naming_two_skills_from_two_modules_given_through_module_paths_spawns_with_both_in_the_order_it_names_them()
    {
        using var temp = new TemporaryHome();
        using StandInModuleDirectory docs = StandInModuleDirectory.Write("docs", "1.0.0");
        using StandInModuleDirectory lint = StandInModuleDirectory.Write("lint", "2.0.0");
        docs.WriteSkill("greet", "Say hello.");
        lint.WriteSkill("check", "Check it.");
        lint.WriteSkill("unnamed", "Not named by the agent.");
        ScriptedExecutor executor = Writes(1);
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor, ModulePaths = [docs.Path, lint.Path] }, None);
        await PrepareAsync(daemon, Writer("check", "greet"), "software", "docs", "lint");
        using var client = new DaemonClient(temp.Home);

        FlowRunView ended = await RunAsync(client, "Two skills", "ended");

        Assert.Equal("written", ended.Outcome);
        ScriptedSpawn spawn = Assert.Single(executor.Spawns);
        Assert.Equal(["check", "greet"], spawn.Skills);
        Assert.Equal(
            [new SessionSkill("check", "lint", Path.Combine(lint.SkillsPath, "check")), new SessionSkill("greet", "docs", Path.Combine(docs.SkillsPath, "greet"))],
            daemon.Services.GetRequiredService<ModuleSkills>().Resolve("writer", ["check", "greet"], Project, ["docs", "lint"], out _));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_composed_modules_skills_are_held_under_the_name_its_manifest_gives_and_an_agent_naming_none_spawns_with_none()
    {
        using var temp = new TemporaryHome();
        string software = CopyOfSoftware(temp);
        Directory.CreateDirectory(Path.Combine(software, "skills", "review-style"));
        File.WriteAllText(Path.Combine(software, "skills", "review-style", "SKILL.md"), "Review the way we review.");
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["writer"] = [Scripts.Answer("done", 1)],
            [SoftwareAgents.Build.Name] = [Scripts.Answer("done", 2)],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, new SoftwareHosting { ModulePath = software }, None);
        await PrepareAsync(daemon, Writer("review-style"), "software");
        await Publishing.FlowAsync(daemon, Project, OneAgentStep with { Name = "plain", Steps = [OneAgentStep.Steps[0] with { Agent = SoftwareAgents.Build.Name }] });
        using var client = new DaemonClient(temp.Home);

        await RunAsync(client, "Composed skill", "ended");
        await RunAsync(client, "No skill", "ended", "plain");

        Assert.Equal([["review-style"], []], executor.Spawns.Select(s => s.Skills));
        Assert.Equal([new ModuleSkill("review-style", Path.Combine(software, "skills", "review-style"))], daemon.Services.GetRequiredService<ModuleSkills>().Of("software"));

        await daemon.StopAsync(None);
    }

    /// <summary>The skills an agent names that cannot be resolved, the modules switched on, and the reason's tail.</summary>
    public static TheoryData<string[], string[], string> Unresolvable() => new()
    {
        { ["greet", "absent"], ["software", "docs", "lint"], $"Agent 'writer' names the skill 'absent', which no module switched on for project '{Project}' ships." },
        // lint ships it, but is not switched on for the project
        { ["check"], ["software", "docs"], $"Agent 'writer' names the skill 'check', which no module switched on for project '{Project}' ships." },
        { ["shared"], ["software", "docs", "lint"], $"Agent 'writer' names the skill 'shared', which the modules 'docs' and 'lint' switched on for project '{Project}' each ship, so which one is meant is not known." },
        { ["greet", "Greet"], ["software", "docs", "lint"], "Agent 'writer' names the skills 'greet' and 'Greet', which differ only in letter case, so a session could not hold both." },
    };

    [Theory]
    [MemberData(nameof(Unresolvable))]
    public async Task A_skill_that_cannot_be_resolved_pauses_before_the_launch_leaving_its_spawn_unused_and_starting_nothing(string[] named, string[] modules, string reason)
    {
        using var temp = new TemporaryHome();
        using StandInModuleDirectory docs = StandInModuleDirectory.Write("docs", "1.0.0");
        using StandInModuleDirectory lint = StandInModuleDirectory.Write("lint", "2.0.0");
        docs.WriteSkill("greet", "Say hello.");
        docs.WriteSkill("shared", "One way.");
        lint.WriteSkill("check", "Check it.");
        lint.WriteSkill("shared", "Another way.");
        ScriptedExecutor executor = Writes(1);
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor, ModulePaths = [docs.Path, lint.Path] }, None);
        await PrepareAsync(daemon, Writer(named), modules);
        using var client = new DaemonClient(temp.Home);

        FlowRunView paused = await RunAsync(client, "Unresolvable", "paused");

        Assert.Equal($"Step 'write' cannot give its session the skills its agent names: {reason}", paused.PauseReason);
        Assert.Equal(FlowRunLevers.PerformAgain("write"), paused.Lever);
        // the spawn the visit counted is left unused, since the launch was never recorded
        Assert.Equal(1, paused.Spawns);
        Assert.Empty(executor.Spawns);

        // once every module the agent's skills need is switched on, alone, performing the step again uses that spawn
        if (named is ["check"])
        {
            await daemon.Services.GetRequiredService<ProjectService>().SetModulesAsync(Principal.LocalOwner, Project, ["software", "docs", "lint"], None);
            await daemon.Services.GetRequiredService<FlowRunService>().ResumeAsync(Principal.LocalOwner, Project, paused.Id, "Switched lint on.", null, None);
            FlowRunView resumed = await FlowRunDriver.UntilAsync(client, Project, paused.Id, "ended");
            Assert.Equal(1, resumed.Spawns);
            Assert.Equal(["check"], Assert.Single(executor.Spawns).Skills);
        }

        await daemon.StopAsync(None);
    }

    private static ScriptedExecutor Writes(int answers) => new(new Dictionary<string, IReadOnlyList<ScriptEntry>>
    {
        ["writer"] = [.. Enumerable.Range(1, answers).Select(n => Scripts.Answer("done", n))],
    });

    /// <summary>Registers the project with <paramref name="modules"/> switched on, and publishes the agent and the one-step flow in it.</summary>
    private static async Task PrepareAsync(RunningDaemon daemon, AgentDefinition agent, params string[] modules)
    {
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await daemon.Services.GetRequiredService<ProjectService>().SetModulesAsync(Principal.LocalOwner, Project, modules, None);
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Agent, DefinitionScope.Project, Project, agent.Name,
            AgentDefinition.CanonicalJson(agent), agent.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        await Publishing.FlowAsync(daemon, Project, OneAgentStep);
    }

    private static async Task<FlowRunView> RunAsync(DaemonClient client, string title, string status, string flow = "write")
    {
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", title, null), None);
        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, flow);
        return await FlowRunDriver.UntilAsync(client, Project, started.Id, status);
    }

    private static async Task StartAndStopAsync(NodeHome home, DaemonOptions options)
    {
        await using RunningDaemon daemon = await Daemons.StartAsync(home, options, None);
        await daemon.StopAsync(None);
    }

    /// <summary>A copy of the software module's directory as the build left it beside the daemon, for a test to add to.</summary>
    private static string CopyOfSoftware(TemporaryHome temp)
    {
        string copy = Path.Combine(temp.Home.Path, "software-module");
        foreach (string file in Directory.EnumerateFiles(ModuleComposition.SoftwareModulePath, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(copy, Path.GetRelativePath(ModuleComposition.SoftwareModulePath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? copy);
            File.Copy(file, target);
        }

        return copy;
    }
}
