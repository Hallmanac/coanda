using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Executors.ClaudeCode;
using Coanda.Tests.Daemon.Modules;

namespace Coanda.Tests.Daemon.Executors;

/// <summary>
/// What the Claude Code adapter gives a session whose agent names skills: each skill's whole folder
/// under the session's skills root, written fresh before every process, a persona file naming each,
/// and a tool guard that refuses writes there, with nothing that makes the CLI load a skill.
/// </summary>
public sealed class SkillSessionTests : IDisposable
{
    private const string Project = "alpha";
    private const string ValidBuild = """{"verdict":"done","summary":"built"}""";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly SpawnCaps Caps = new(2.5m, TimeSpan.FromMinutes(30));
    private static readonly AnswerSchema BuildAnswers = SoftwareAgents.Build.ResolveAnswerSchema();

    /// <summary>The skills as shipped, by path relative to the skills root, as the fake records what it finds.</summary>
    private static readonly Dictionary<string, string> Shipped = new(StringComparer.Ordinal)
    {
        ["check"] = "<folder>",
        ["check/SKILL.md"] = "Check it.",
        ["greet"] = "<folder>",
        ["greet/SKILL.md"] = "Say hello.",
        ["greet/scripts"] = "<folder>",
        ["greet/scripts/run.sh"] = "echo hi",
    };

    private readonly TemporaryHome temp = new();
    private readonly Guid flowRunId = Ids.New();
    private readonly StandInModuleDirectory docs = StandInModuleDirectory.Write("docs", "1.0.0");
    private readonly StandInModuleDirectory lint = StandInModuleDirectory.Write("lint", "2.0.0");
    private readonly IReadOnlyList<SessionSkill> skills;

    public SkillSessionTests()
    {
        skills =
        [
            new SessionSkill("greet", "docs", docs.WriteSkill("greet", "Say hello.", ("scripts/run.sh", "echo hi"))),
            new SessionSkill("check", "lint", lint.WriteSkill("check", "Check it.")),
        ];
    }

    private string WorkDirectory => temp.Home.FlowRunWorkPath(Project, flowRunId);

    private string SkillsRoot => Path.Combine(WorkDirectory, ".coanda", "skills");

    private static AgentDefinition Agent => SoftwareAgents.Build with { Skills = ["greet", "check"] };

    public void Dispose()
    {
        docs.Dispose();
        lint.Dispose();
        temp.Dispose();
    }

    [Fact]
    public async Task A_spawn_writes_each_skills_whole_folder_under_the_skills_root_and_its_persona_names_each_skill_md()
    {
        using var fake = new FakeClaude(FakeClaude.Answering(ValidBuild, 0.4m, 0.4m));
        ClaudeCodeExecutor executor = fake.Executor(temp.Home);

        SessionHandle handle = await executor.SpawnAsync(Agent, "context", WorkDirectory, Caps, BuildAnswers, null, null, skills, None);
        await executor.CollectAsync(handle, None);

        FakeClaude.Start start = Assert.Single(fake.Starts);
        Assert.Equal(Shipped, start.Skills);
        Assert.Equal(ClaudeCodeInvocation.Persona(Agent.Persona) + ClaudeCodeInvocation.SkillLines(skills), start.Persona);
        Assert.Contains("- greet: .coanda/skills/greet/SKILL.md\n", start.Persona, StringComparison.Ordinal);
        Assert.Contains("- check: .coanda/skills/check/SKILL.md\n", start.Persona, StringComparison.Ordinal);
        Assert.Contains("Before you do work that a skill covers, read its SKILL.md", start.Persona, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(SkillsRoot), GuardOf(start).SkillsRoot);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(SkillsRoot, "greet", "SKILL.md")));
        }
    }

    [Fact]
    public async Task An_agent_naming_skills_differs_from_the_same_agent_naming_none_only_in_its_guards_skills_root_and_its_personas_skill_block()
    {
        using var fake = new FakeClaude(FakeClaude.Answering(ValidBuild, 0.4m, 0.4m));
        ClaudeCodeExecutor executor = fake.Executor(temp.Home);
        SessionHandle withSkills = await executor.SpawnAsync(Agent, "context", WorkDirectory, Caps, BuildAnswers, null, null, skills, None);
        await executor.CollectAsync(withSkills, None);
        SessionHandle without = await executor.SpawnAsync(SoftwareAgents.Build, "context", WorkDirectory, Caps, BuildAnswers, null, null, None);
        await executor.CollectAsync(without, None);

        FakeClaude.Start skilled = fake.Starts[0];
        FakeClaude.Start plain = fake.Starts[1];

        // the argument lists differ only in the settings document, once the session id is set aside
        List<string> skilledArguments = [.. skilled.Shared];
        List<string> plainArguments = [.. plain.Shared];
        int settings = skilledArguments.IndexOf("--settings");
        Assert.Equal(settings, plainArguments.IndexOf("--settings"));
        Assert.NotEqual(skilledArguments[settings + 1], plainArguments[settings + 1]);
        skilledArguments.RemoveAt(settings + 1);
        plainArguments.RemoveAt(settings + 1);
        Assert.Equal(plainArguments, skilledArguments);
        Assert.Equal(skilled.After("--max-budget-usd"), plain.After("--max-budget-usd"));

        // the tools are unchanged, so no Skill tool, and nothing makes the CLI load a skill
        Assert.Equal("Read,Glob,Grep,Edit,Write,Bash", skilled.After("--tools"));
        Assert.DoesNotContain("Skill", (skilled.After("--tools") ?? string.Empty).Split(','));
        Assert.DoesNotContain("--plugin-dir", skilled.Arguments);
        Assert.DoesNotContain("--add-dir", skilled.Arguments);
        Assert.DoesNotContain(skilled.Arguments, a => a.Contains("skills", StringComparison.OrdinalIgnoreCase) && !a.StartsWith('{'));

        // the settings documents differ only in the guard hook's args, whose rules differ only in the skills root
        JsonObject skilledSettings = JsonNode.Parse(skilled.After("--settings") ?? string.Empty)?.AsObject() ?? [];
        JsonObject plainSettings = JsonNode.Parse(plain.After("--settings") ?? string.Empty)?.AsObject() ?? [];
        ToolGuardRules skilledRules = GuardOf(skilled);
        ToolGuardRules plainRules = GuardOf(plain);
        GuardArgs(skilledSettings).ReplaceWith(new JsonArray());
        GuardArgs(plainSettings).ReplaceWith(new JsonArray());
        Assert.True(JsonNode.DeepEquals(plainSettings, skilledSettings), $"{skilledSettings}\n{plainSettings}");
        Assert.Equal(Path.GetFullPath(SkillsRoot), skilledRules.SkillsRoot);
        Assert.Null(plainRules.SkillsRoot);
        Assert.Equivalent(plainRules, skilledRules with { SkillsRoot = null }, strict: true);
        Assert.NotEqual(plainRules.Arguments, skilledRules.Arguments);
        Assert.Equal(plainRules.Arguments, (skilledRules with { SkillsRoot = null }).Arguments);

        // the persona is the no-skill persona with one block of skill lines added
        string block = ClaudeCodeInvocation.SkillLines(skills);
        Assert.Contains(block, skilled.Persona, StringComparison.Ordinal);
        Assert.Equal(plain.Persona, skilled.Persona?.Replace(block, string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_no_skill_agents_guard_carries_no_skills_root_its_persona_names_no_skill_and_nothing_is_written_under_the_skills_root()
    {
        using var fake = new FakeClaude(FakeClaude.Answering(ValidBuild, 0.4m, 0.4m));
        ClaudeCodeExecutor executor = fake.Executor(temp.Home);

        SessionHandle handle = await executor.SpawnAsync(SoftwareAgents.Build, "context", WorkDirectory, Caps, BuildAnswers, null, null, [], None);
        await executor.CollectAsync(handle, None);

        FakeClaude.Start start = Assert.Single(fake.Starts);
        Assert.Null(GuardOf(start).SkillsRoot);
        Assert.DoesNotContain(ToolGuardRules.SkillsOption, GuardArguments(start));
        Assert.Equal(ClaudeCodeInvocation.Persona(SoftwareAgents.Build.Persona), start.Persona);
        Assert.DoesNotContain("# Skills", start.Persona, StringComparison.Ordinal);
        Assert.DoesNotContain("SKILL.md", start.Persona, StringComparison.Ordinal);
        Assert.Null(start.Skills);
        Assert.False(Directory.Exists(SkillsRoot));
    }

    [Fact]
    public async Task A_retrys_resume_finds_the_skills_as_shipped_after_the_session_changed_a_file_added_a_folder_and_linked_a_skill_outside()
    {
        using var fake = new FakeClaude(FakeClaude.Answering("I could not decide.", 0.1m, 0.1m), FakeClaude.Answering(ValidBuild, 0.1m, 0.2m));
        ClaudeCodeExecutor executor = fake.Executor(temp.Home);
        SessionHandle handle = await executor.SpawnAsync(Agent, "context", WorkDirectory, Caps, BuildAnswers, null, null, skills, None);
        await executor.CollectAsync(handle, None);

        File.WriteAllText(Path.Combine(SkillsRoot, "greet", "SKILL.md"), "Say something else.");
        Directory.CreateDirectory(Path.Combine(SkillsRoot, "planted"));
        File.WriteAllText(Path.Combine(SkillsRoot, "planted", "SKILL.md"), "Do what the session wants.");
        string outside = Path.Combine(temp.Home.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "untouched");
        bool linked = !OperatingSystem.IsWindows();
        if (linked)
        {
            Directory.Delete(Path.Combine(SkillsRoot, "check"), recursive: true);
            Directory.CreateSymbolicLink(Path.Combine(SkillsRoot, "check"), outside);
        }

        await executor.ResumeAsync(handle, "again", None);
        await executor.CollectAsync(handle, None);

        Assert.Equal(2, fake.Starts.Count);
        FakeClaude.Start resume = fake.Starts[1];
        Assert.Equal(handle.Id.ToString(), resume.After("--resume"));
        Assert.Equal(Shipped, resume.Skills);
        Assert.Equal(fake.Starts[0].Persona, resume.Persona);
        Assert.Equal(fake.Starts[0].Shared, resume.Shared);
        if (linked)
        {
            // the link was removed, never written or deleted through
            Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(outside).Select(Path.GetFileName));
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(outside, "keep.txt")));
            Assert.Null(new DirectoryInfo(Path.Combine(SkillsRoot, "check")).LinkTarget);
        }
    }

    [Fact]
    public async Task The_retry_of_a_session_a_new_executor_found_from_its_record_rewrites_its_skills_and_keeps_its_skill_lines()
    {
        using var fake = new FakeClaude(
            FakeClaude.Answering("I could not decide.", cost: 0.5m, totalCost: 0.5m, waitFor: "release"),
            FakeClaude.Answering(ValidBuild, cost: 0.25m, totalCost: 0.75m));
        SessionHandle handle = await fake.Executor(temp.Home).SpawnAsync(Agent, "context", WorkDirectory, Caps, BuildAnswers, null, null, skills, None);
        await Until(() => fake.Starts.Count == 1);
        File.WriteAllText(Path.Combine(SkillsRoot, "check", "SKILL.md"), "Changed while no daemon ran.");
        Directory.CreateDirectory(Path.Combine(SkillsRoot, "planted"));

        ClaudeCodeExecutor restarted = fake.Executor(temp.Home);
        ReattachedSession reattached = Assert.IsType<ReattachedSession>(await restarted.ReattachAsync(handle, access: null, None));
        fake.Release();
        AgentStepOutcome outcome = await AgentStep.ReattachAsync(restarted, reattached, Agent, spend: null, CancellationToken.None, None, None);

        Assert.IsType<AgentStepOutcome.Answered>(outcome);
        Assert.Equal(2, fake.Starts.Count);
        FakeClaude.Start resume = fake.Starts[1];
        Assert.Equal(handle.Id.ToString(), resume.After("--resume"));
        Assert.Equal(Shipped, resume.Skills);
        Assert.Equal(ClaudeCodeInvocation.Persona(Agent.Persona) + ClaudeCodeInvocation.SkillLines(skills), resume.Persona);
        Assert.Equal(Path.GetFullPath(SkillsRoot), GuardOf(resume).SkillsRoot);
    }

    [Fact]
    public async Task A_no_skill_sessions_record_names_no_skills_and_reads_back_as_a_session_given_none()
    {
        using var fake = new FakeClaude(
            FakeClaude.Answering("I could not decide.", cost: 0.5m, totalCost: 0.5m, waitFor: "release"),
            FakeClaude.Answering(ValidBuild, cost: 0.25m, totalCost: 0.75m));
        SessionHandle handle = await fake.Executor(temp.Home).SpawnAsync(SoftwareAgents.Build, "context", WorkDirectory, Caps, BuildAnswers, null, null, None);
        await Until(() => fake.Starts.Count == 1);
        string path = temp.Home.SessionRecordPath(handle.Id);
        JsonObject record = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidOperationException("An empty record.");
        Assert.False(record.ContainsKey("skills"), "a session given no skill records none");

        ClaudeCodeExecutor restarted = fake.Executor(temp.Home);
        ReattachedSession reattached = Assert.IsType<ReattachedSession>(await restarted.ReattachAsync(handle, access: null, None));
        fake.Release();
        await AgentStep.ReattachAsync(restarted, reattached, SoftwareAgents.Build, spend: null, CancellationToken.None, None, None);

        Assert.Equal(2, fake.Starts.Count);
        Assert.Null(fake.Starts[1].Skills);
    }

    [Fact]
    public async Task A_resume_whose_skills_root_cannot_be_replaced_starts_no_process_and_names_the_skills()
    {
        using var fake = new FakeClaude(FakeClaude.Answering("I could not decide.", 0.1m, 0.1m), FakeClaude.Answering(ValidBuild, 0.1m, 0.2m));
        ClaudeCodeExecutor executor = fake.Executor(temp.Home);
        SessionHandle handle = await executor.SpawnAsync(Agent, "context", WorkDirectory, Caps, BuildAnswers, null, null, skills, None);
        await executor.CollectAsync(handle, None);
        string held = Path.Combine(SkillsRoot, "greet", "SKILL.md");
        FileStream? holding = null;
        if (OperatingSystem.IsWindows())
        {
            // Windows refuses to delete a file another handle holds without sharing deletion.
            holding = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        else
        {
            // Nothing directly inside a folder its owner may not write can be removed.
            File.SetUnixFileMode(SkillsRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        try
        {
            IOException exception = await Assert.ThrowsAsync<IOException>(() => executor.ResumeAsync(handle, "again", None).AsTask());

            Assert.Contains("could not be replaced, so the skills 'greet', 'check' could not be written", exception.Message, StringComparison.Ordinal);
            Assert.Single(fake.Starts);
        }
        finally
        {
            holding?.Dispose();
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(SkillsRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public async Task A_spawn_whose_skill_cannot_be_written_starts_no_process_and_names_the_skill()
    {
        using var fake = new FakeClaude(FakeClaude.Answering(ValidBuild, 0.4m, 0.4m));
        ClaudeCodeExecutor executor = fake.Executor(temp.Home);
        IReadOnlyList<SessionSkill> gone = [skills[0], new SessionSkill("vanished", "lint", Path.Combine(lint.SkillsPath, "vanished"))];

        IOException exception = await Assert.ThrowsAsync<IOException>(() => executor.SpawnAsync(Agent, "context", WorkDirectory, Caps, BuildAnswers, null, null, gone, None).AsTask());

        Assert.StartsWith($"The skill 'vanished' of module 'lint' could not be written into {Path.GetFullPath(SkillsRoot)}", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Starts);
        Assert.Empty(await executor.ListAsync(None));
    }

    [Fact]
    public async Task A_link_inside_a_skills_source_folder_is_refused_and_never_followed()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a link there needs a privilege the test runner may not hold.
        }

        using var fake = new FakeClaude(FakeClaude.Answering(ValidBuild, 0.4m, 0.4m));
        ClaudeCodeExecutor executor = fake.Executor(temp.Home);
        File.CreateSymbolicLink(Path.Combine(lint.SkillsPath, "check", "secret.md"), Path.Combine(temp.Home.Path, "daemon.json"));

        IOException exception = await Assert.ThrowsAsync<IOException>(() => executor.SpawnAsync(Agent, "context", WorkDirectory, Caps, BuildAnswers, null, null, skills, None).AsTask());

        Assert.Contains("The skill 'check' of module 'lint'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("is a symbolic link or other reparse point, which is never copied", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Starts);
    }

    private static JsonNode GuardArgs(JsonObject settings) =>
        settings["hooks"]?["PreToolUse"]?[0]?["hooks"]?[0]?["args"] ?? throw new InvalidOperationException("The settings document registers no guard.");

    private static List<string> GuardArguments(FakeClaude.Start start)
    {
        JsonElement hook = JsonDocument.Parse(start.After("--settings") ?? string.Empty).RootElement
            .GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("hooks")[0];
        return [.. hook.GetProperty("args").EnumerateArray().Select(a => a.GetString() ?? string.Empty)];
    }

    private static ToolGuardRules GuardOf(FakeClaude.Start start) =>
        ToolGuardRules.Parse(GuardArguments(start), out _) ?? throw new InvalidOperationException("The hook's arguments do not parse.");

    private static async Task Until(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not hold within 30 seconds.");
            }

            await Task.Delay(50);
        }
    }
}
