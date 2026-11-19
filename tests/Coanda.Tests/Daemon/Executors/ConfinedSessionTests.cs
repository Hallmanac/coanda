using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Daemon;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Executors.ClaudeCode;

namespace Coanda.Tests.Daemon.Executors;

/// <summary>
/// What the Claude Code adapter starts a confined agent's session with, at its spawn, at its retry,
/// and at a resume after a restart: the tools, the settings document's guard, and the environment.
/// The fake claude is reached through a file in the work directory, since a confined environment
/// carries no variable naming its control directory.
/// </summary>
public sealed class ConfinedSessionTests : IDisposable
{
    private const string Project = "alpha";
    private const string ValidBuild = """{"verdict":"done","summary":"built"}""";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly SpawnCaps Caps = new(2.5m, TimeSpan.FromMinutes(30));
    private static readonly AnswerSchema BuildAnswers = SoftwareAgents.Build.ResolveAnswerSchema();

    // Built with `with`, which skips validation, so it holds every catalog tool, the shell and the web among them.
    private static readonly AgentDefinition Confined = SoftwareAgents.Build with
    {
        Tools = [ToolCatalog.Read, ToolCatalog.Search, ToolCatalog.Edit, ToolCatalog.Shell, ToolCatalog.Web],
        Confined = true,
    };

    private static readonly string HomeVariable = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";

    private readonly TemporaryHome temp = new();
    private readonly Guid flowRunId = Ids.New();

    private string WorkDirectory => temp.Home.FlowRunWorkPath(Project, flowRunId);

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task A_confined_spawn_and_its_retry_offer_and_pre_approve_no_shell_or_web_tool_and_guard_every_tool_call()
    {
        using FakeClaude fake = Fake(FakeClaude.Answering("I could not decide.", 0.5m, 0.5m), FakeClaude.Answering(ValidBuild, 0.25m, 0.75m));
        ClaudeCodeExecutor executor = fake.Executor(temp.Home, Inherited(fake));

        AgentStepOutcome outcome = await AgentStep.RunAsync(executor, Request(Confined), None);

        Assert.IsType<AgentStepOutcome.Answered>(outcome);
        Assert.Equal(2, fake.Starts.Count);
        Assert.All(fake.Starts, start =>
        {
            Assert.Equal("Read,Glob,Grep,Edit,Write", start.After("--tools"));
            JsonElement settings = Settings(start);
            Assert.Equal(["Read", "Glob", "Grep", "Edit", "Write"], settings.GetProperty("permissions").GetProperty("allow").EnumerateArray().Select(a => a.GetString()));
            JsonElement guard = settings.GetProperty("hooks").GetProperty("PreToolUse")[0];
            Assert.Equal("*", guard.GetProperty("matcher").GetString());
            Assert.True(GuardOf(start).Confined);
        });
    }

    [Fact]
    public async Task An_unconfined_agent_holding_the_same_tools_keeps_them_and_its_six_tool_matcher()
    {
        using var fake = new FakeClaude(FakeClaude.Answering(ValidBuild, 0.1m, 0.1m));
        ClaudeCodeExecutor executor = fake.Executor(temp.Home);

        SessionHandle handle = await executor.SpawnAsync(Confined with { Confined = false }, "context", WorkDirectory, Caps, BuildAnswers, null, null, None);
        await executor.CollectAsync(handle, None);

        FakeClaude.Start start = Assert.Single(fake.Starts);
        Assert.Equal("Read,Glob,Grep,Edit,Write,Bash,WebFetch,WebSearch", start.After("--tools"));
        Assert.Equal("Read|Edit|Write|Glob|Grep|Bash", Settings(start).GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("matcher").GetString());
        Assert.False(GuardOf(start).Confined);
    }

    [Fact]
    public async Task A_confined_spawn_carries_only_listed_inherited_variables_with_the_coa_directory_first_on_the_path_and_no_session_access()
    {
        using FakeClaude fake = Fake(FakeClaude.Answering(ValidBuild, 0.1m, 0.1m));
        string coa = Path.Combine(temp.Home.Path, "coa-bin");
        Dictionary<string, string> inherited = Inherited(fake);
        ClaudeCodeExecutor executor = fake.Executor(temp.Home, inherited, coa);
        var access = new SessionAccess("issued-session-token", temp.Home.Path, Project);

        SessionHandle handle = await executor.SpawnAsync(Confined, "context", WorkDirectory, Caps, BuildAnswers, null, access, None);
        await executor.CollectAsync(handle, None);

        FakeClaude.Start start = Assert.Single(fake.Starts);
        OnlyListed(start, inherited);
        Assert.DoesNotContain(start.Environment.Keys, key => Is(key, "GH_TOKEN"));
        Assert.DoesNotContain(start.Environment.Keys, key => Is(key, Coanda.Testing.FakeClaude.Program.ControlVariable));
        Assert.Equal(temp.Home.Path, Variable(start, HomeVariable));
        Assert.Equal(coa, Variable(start, "PATH")?.Split(Path.PathSeparator)[0]);
        Assert.DoesNotContain(start.Environment.Keys, key => access.Variables.Keys.Any(name => Is(key, name)));
    }

    [Fact]
    public async Task A_confined_spawn_with_a_profile_carries_its_config_directory_and_no_credential_variable()
    {
        using FakeClaude fake = Fake(FakeClaude.Answering(ValidBuild, 0.1m, 0.1m));
        ClaudeCodeExecutor executor = fake.Executor(temp.Home, Inherited(fake, [.. ClaudeCodeInvocation.CredentialVariables.Select(name => (name, "inherited-placeholder"))]));
        var profile = new CredentialProfile("claude-code", "work", Path.Combine(temp.Home.Path, "profiles", "work"), null);

        SessionHandle handle = await executor.SpawnAsync(Confined, "context", WorkDirectory, Caps, BuildAnswers, profile, null, None);
        await executor.CollectAsync(handle, None);

        FakeClaude.Start start = Assert.Single(fake.Starts);
        Assert.Equal(profile.ConfigDirectory, Variable(start, ClaudeCodeInvocation.ConfigDirectoryVariable));
        Assert.DoesNotContain(start.Environment.Keys, key => ClaudeCodeInvocation.CredentialVariables.Any(name => Is(key, name)));
    }

    [Fact]
    public async Task A_confined_spawn_with_no_profile_carries_the_credential_variables_it_inherited()
    {
        using FakeClaude fake = Fake(FakeClaude.Answering(ValidBuild, 0.1m, 0.1m));
        ClaudeCodeExecutor executor = fake.Executor(temp.Home, Inherited(fake, ("ANTHROPIC_API_KEY", "sk-inherited"), ("CLAUDE_CONFIG_DIR", "/inherited/config")));

        SessionHandle handle = await executor.SpawnAsync(Confined, "context", WorkDirectory, Caps, BuildAnswers, null, null, None);
        await executor.CollectAsync(handle, None);

        FakeClaude.Start start = Assert.Single(fake.Starts);
        Assert.Equal("sk-inherited", Variable(start, "ANTHROPIC_API_KEY"));
        Assert.Equal("/inherited/config", Variable(start, "CLAUDE_CONFIG_DIR"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_resume_of_a_confined_session_found_from_its_record_after_a_restart_still_runs_in_the_confined_environment(bool freshAccess)
    {
        using FakeClaude fake = Fake(
            FakeClaude.Answering("I could not decide.", cost: 0.5m, totalCost: 0.5m, waitFor: "release"),
            FakeClaude.Answering(ValidBuild, cost: 0.25m, totalCost: 0.75m));
        string coa = Path.Combine(temp.Home.Path, "coa-bin");
        SessionHandle handle = await fake.Executor(temp.Home, Inherited(fake), coa).SpawnAsync(Confined, "context", WorkDirectory, Caps, BuildAnswers, null, null, None);
        await Until(() => fake.Starts.Count == 1);

        Dictionary<string, string> inherited = Inherited(fake);
        ClaudeCodeExecutor restarted = fake.Executor(temp.Home, inherited, coa);
        SessionAccess? access = freshAccess ? new SessionAccess("second-daemon-token", temp.Home.Path, Project) : null;
        ReattachedSession reattached = Assert.IsType<ReattachedSession>(await restarted.ReattachAsync(handle, access, None));
        fake.Release();
        AgentStepOutcome outcome = await AgentStep.ReattachAsync(restarted, reattached, Confined, spend: null, CancellationToken.None, None, None);

        Assert.IsType<AgentStepOutcome.Answered>(outcome);
        Assert.Equal(2, fake.Starts.Count);
        FakeClaude.Start resume = fake.Starts[1];
        Assert.Equal(handle.Id.ToString(), resume.After("--resume"));
        Assert.Equal(fake.Starts[0].Shared, resume.Shared);
        OnlyListed(resume, inherited);
        Assert.DoesNotContain(resume.Environment.Keys, key => Is(key, "GH_TOKEN"));
        Assert.Equal(temp.Home.Path, Variable(resume, HomeVariable));
        Assert.NotNull(Variable(resume, "PATH"));
        Assert.Equal(coa, Variable(resume, "PATH")?.Split(Path.PathSeparator)[0]);
        Assert.DoesNotContain(resume.Environment.Keys, key => Is(key, SessionAccess.TokenVariable) || Is(key, SessionAccess.HomeVariable) || Is(key, SessionAccess.ProjectVariable));
    }

    [Fact]
    public async Task A_session_record_written_before_confinement_was_kept_reads_back_as_an_unconfined_session()
    {
        using var fake = new FakeClaude(
            FakeClaude.Answering("I could not decide.", cost: 0.5m, totalCost: 0.5m, waitFor: "release"),
            FakeClaude.Answering(ValidBuild, cost: 0.25m, totalCost: 0.75m));
        Dictionary<string, string> inherited = fake.Environment(("GH_TOKEN", "inherited-gh-token"));
        SessionHandle handle = await fake.Executor(temp.Home, inherited).SpawnAsync(SoftwareAgents.Build, "context", WorkDirectory, Caps, BuildAnswers, null, null, None);
        await Until(() => fake.Starts.Count == 1);

        // A record as the daemon before this change wrote it: the same members, with no confinement.
        string path = temp.Home.SessionRecordPath(handle.Id);
        JsonObject record = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidOperationException("An empty record.");
        Assert.True(record.Remove("confined"), "the record keeps whether the session is confined");
        File.WriteAllText(path, record.ToJsonString());

        ClaudeCodeExecutor restarted = fake.Executor(temp.Home, inherited);
        ReattachedSession reattached = Assert.IsType<ReattachedSession>(await restarted.ReattachAsync(handle, access: null, None));
        fake.Release();
        await AgentStep.ReattachAsync(restarted, reattached, SoftwareAgents.Build, spend: null, CancellationToken.None, None, None);

        Assert.Equal(2, fake.Starts.Count);
        Assert.Equal("inherited-gh-token", Variable(fake.Starts[1], "GH_TOKEN"));
    }

    [Fact]
    public void The_allowed_list_names_no_variable_only_a_test_uses_and_none_session_access_sets()
    {
        Assert.DoesNotContain(Coanda.Testing.FakeClaude.Program.ControlVariable, ClaudeCodeInvocation.ConfinedEnvironmentVariables);
        Assert.DoesNotContain(Coanda.Testing.FakeClaude.Program.IgnoreInputVariable, ClaudeCodeInvocation.ConfinedEnvironmentVariables);
        var access = new SessionAccess("token", "/home", Project);
        Assert.Empty(access.Variables.Keys.Intersect(ClaudeCodeInvocation.ConfinedEnvironmentVariables));
        Assert.Empty(ClaudeCodeInvocation.EnclosingSessionVariables.Intersect(ClaudeCodeInvocation.ConfinedEnvironmentVariables));
        Assert.Empty(ClaudeCodeInvocation.HookSkippingVariables.Intersect(ClaudeCodeInvocation.ConfinedEnvironmentVariables));
        Assert.Equal(ClaudeCodeInvocation.ConfinedEnvironmentVariables.Count, ClaudeCodeInvocation.ConfinedEnvironmentVariables.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_confined_agent_with_unconfined_guard_rules_is_refused()
    {
        ToolGuardRules unconfined = ClaudeCodeInvocation.Guard(temp.Home, WorkDirectory, null, new Dictionary<string, string>(), []);

        Assert.Throws<ArgumentException>(() => ClaudeCodeInvocation.SessionArguments(Confined, null, unconfined, "/bin/coa"));
        Assert.Throws<ArgumentException>(() => ClaudeCodeInvocation.SessionArguments(SoftwareAgents.Build, null, unconfined with { Confined = true }, "/bin/coa"));
    }

    /// <summary>
    /// The built <c>coa</c> run as a confined session's tool guard, in exactly the environment a
    /// confined spawn built from this test process's own environment carries, allows a Read in the
    /// work directory: the allowed list is enough for the apphost to find its runtime.
    /// </summary>
    [Fact]
    public async Task The_built_coa_tool_guard_runs_in_a_confined_spawns_environment_and_allows_a_read_inside_the_work_directory()
    {
        string coaDirectory = AppContext.BaseDirectory;
        string coa = ClaudeCodeExecutor.CoaExecutable(coaDirectory);
        Directory.CreateDirectory(WorkDirectory);
        string file = Path.Combine(WorkDirectory, "a.cs");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await File.WriteAllTextAsync(file, "class A;", timeout.Token);
        IReadOnlyDictionary<string, string> inherited = ClaudeCodeExecutor.CurrentEnvironment();
        ToolGuardRules rules = ClaudeCodeInvocation.Guard(temp.Home, WorkDirectory, null, inherited, [], confined: true);
        IReadOnlyDictionary<string, string> environment = ClaudeCodeInvocation.Environment(inherited, null, access: null, coaDirectory, confined: true);

        var start = new ProcessStartInfo(coa) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in rules.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        foreach ((string name, string value) in environment)
        {
            start.Environment[name] = value;
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("coa did not start.");
        string call = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = "3f6c1b2a-0000-4000-8000-000000000001",
            ["transcript_path"] = Path.Combine(temp.Home.Path, "config", "projects", "-work", "3f6c1b2a-0000-4000-8000-000000000001.jsonl"),
            ["cwd"] = WorkDirectory,
            ["hook_event_name"] = "PreToolUse",
            ["tool_name"] = "Read",
            ["tool_input"] = new { file_path = file },
        });
        string error;
        string output;
        try
        {
            await process.StandardInput.WriteAsync(call.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            Task<string> reading = process.StandardError.ReadToEndAsync(timeout.Token);
            output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            error = await reading;
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Disposing the process does not end it, so a coa that hangs is stopped here.
            process.Kill(entireProcessTree: true);
            throw;
        }

        Assert.True(process.ExitCode == ToolGuardOutcome.AllowCode, $"coa exited {process.ExitCode}: {error}{output}");
    }

    private FakeClaude Fake(params object[] script)
    {
        var fake = new FakeClaude(script);
        fake.ControlFrom(WorkDirectory);
        return fake;
    }

    /// <summary>The fake's environment with a home directory and a variable the allowed list does not name, as a daemon would inherit them.</summary>
    private Dictionary<string, string> Inherited(FakeClaude fake, params (string Name, string Value)[] more) =>
        fake.Environment([(HomeVariable, temp.Home.Path), ("GH_TOKEN", "inherited-gh-token"), .. more]);

    private AgentStepRequest Request(AgentDefinition agent) =>
        new(agent, "Step: build", WorkDirectory, Caps, NameOnlyAnswerSchemas.Build);

    /// <summary>
    /// Every variable the start's process holds that the daemon inherited is on the allowed list. The
    /// system may add variables of its own to every process it starts (macOS's
    /// <c>__CF_USER_TEXT_ENCODING</c>), which nothing inherited.
    /// </summary>
    private static void OnlyListed(FakeClaude.Start start, IReadOnlyDictionary<string, string> inherited) =>
        Assert.All(
            start.Environment.Keys.Where(key => inherited.Keys.Any(name => Is(key, name))),
            key => Assert.True(ClaudeCodeInvocation.ConfinedEnvironmentVariables.Any(name => Is(key, name)), $"'{key}' is not on the allowed list"));

    private static bool Is(string key, string name) =>
        string.Equals(key, name, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? Variable(FakeClaude.Start start, string name) =>
        start.Environment.FirstOrDefault(v => Is(v.Key, name)).Value;

    private static JsonElement Settings(FakeClaude.Start start) =>
        JsonDocument.Parse(start.After("--settings") ?? string.Empty).RootElement;

    private static ToolGuardRules GuardOf(FakeClaude.Start start)
    {
        JsonElement hook = Settings(start).GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("hooks")[0];
        List<string> arguments = [.. hook.GetProperty("args").EnumerateArray().Select(a => a.GetString() ?? string.Empty)];
        return ToolGuardRules.Parse(arguments, out _) ?? throw new InvalidOperationException("The hook's arguments do not parse.");
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            try
            {
                await Task.Delay(50, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("The condition did not hold within 30 seconds.");
            }
        }
    }
}
