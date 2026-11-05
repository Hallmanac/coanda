using Coanda.Core;
using Coanda.Daemon.Api;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Executors.ClaudeCode;
using Coanda.Daemon.Transport;

namespace Coanda.Tests.Daemon;

public sealed class NodeConfigurationTests
{
    [Fact]
    public void A_home_with_no_configuration_runs_claude_from_the_path_and_holds_no_profiles()
    {
        using var temp = new TemporaryHome();

        DaemonOptions options = NodeConfiguration.Load(temp.Home);

        // a cold install runs its agent steps on Claude Code
        Assert.Equal("claude", Assert.IsType<ClaudeCodeExecutor>(options.Executor).Executable);
        Assert.Empty(options.Profiles);
        Assert.Empty(options.Gates);
        Assert.Equal(DaemonOptions.DefaultAgentStepLimit, options.AgentStepLimit);
        Assert.Equal(LedgerExport.DefaultInterval, options.ExportInterval);
        Assert.Equal(LedgerImport.DefaultInterval, options.ImportInterval);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(""", "ledgerExport": true""", true)]
    [InlineData(""", "ledgerExport": false""", false)]
    public void The_ledger_export_runs_unless_the_configuration_turns_it_off(string setting, bool exports)
    {
        using var temp = new TemporaryHome();
        Write(temp, $$"""{ "executor": "scripted"{{setting}} }""");

        DaemonOptions options = NodeConfiguration.Load(temp.Home);

        Assert.Equal(exports ? LedgerExport.DefaultInterval : (TimeSpan?)null, options.ExportInterval);
        Assert.Equal(LedgerImport.DefaultInterval, options.ImportInterval);
    }

    [Theory]
    [InlineData("\"no\"")]
    [InlineData("null")]
    [InlineData("0")]
    public void A_ledger_export_that_is_not_true_or_false_is_refused(string setting)
    {
        using var temp = new TemporaryHome();
        Write(temp, $$"""{ "executor": "scripted", "ledgerExport": {{setting}} }""");

        Assert.Throws<DomainException>(() => NodeConfiguration.Load(temp.Home));
    }

    [Fact]
    public void A_configuration_that_selects_claude_code_with_no_executable_runs_claude_from_the_path()
    {
        using var temp = new TemporaryHome();
        Write(temp, """{ "executor": "claude-code", "profiles": { "claude-code": [ { "name": "work", "configDirectory": "accounts/work" } ] } }""");

        DaemonOptions options = NodeConfiguration.Load(temp.Home);

        Assert.Equal("claude", Assert.IsType<ClaudeCodeExecutor>(options.Executor).Executable);
        Assert.Equal("work", Assert.Single(options.Profiles).Name);
    }

    [Theory]
    [InlineData("claude-beta", "claude-beta")]
    [InlineData("bin/claude", "<home>/bin/claude")]
    public void A_configured_claude_executable_is_a_name_looked_up_on_the_path_or_a_path_resolved_against_the_home(string configured, string expected)
    {
        using var temp = new TemporaryHome();
        Write(temp, $$"""{ "executor": "claude-code", "claudeExecutable": {{System.Text.Json.JsonSerializer.Serialize(configured)}} }""");

        DaemonOptions options = NodeConfiguration.Load(temp.Home);

        Assert.Equal(expected.Replace("<home>/", temp.Home.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal).Replace('/', Path.DirectorySeparatorChar), Assert.IsType<ClaudeCodeExecutor>(options.Executor).Executable);
    }

    [Fact]
    public void An_absolute_claude_executable_is_kept_as_given()
    {
        using var temp = new TemporaryHome();
        string absolute = Path.Combine(Path.GetTempPath(), "tools", OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        Write(temp, $$"""{ "executor": "claude-code", "claudeExecutable": {{System.Text.Json.JsonSerializer.Serialize(absolute)}} }""");

        Assert.Equal(absolute, Assert.IsType<ClaudeCodeExecutor>(NodeConfiguration.Load(temp.Home).Executor).Executable);
    }

    [Fact]
    public void A_configuration_that_selects_scripted_loads_its_profiles_with_a_relative_config_directory_resolved_against_the_home()
    {
        using var temp = new TemporaryHome();
        string absolute = Path.Combine(Path.GetTempPath(), "elsewhere", ".claude-personal");
        Write(temp, $$"""
            {
              "executor": "scripted",
              "profiles": {
                "claude-code": [
                  { "name": " work ", "configDirectory": "accounts/work" },
                  { "name": "personal", "configDirectory": {{System.Text.Json.JsonSerializer.Serialize(absolute)}} },
                  { "name": "ci", "apiKeyHelper": "op read op://ci/anthropic/key" }
                ]
              }
            }
            """);

        DaemonOptions options = NodeConfiguration.Load(temp.Home);

        Assert.IsType<ScriptedExecutor>(options.Executor);
        Assert.Equal([new CredentialProfile("claude-code", "work", Path.Combine(temp.Home.Path, "accounts", "work"), null), new CredentialProfile("claude-code", "personal", absolute, null), new CredentialProfile("claude-code", "ci", null, "op read op://ci/anthropic/key")], options.Profiles);
        Assert.All(options.Profiles.Select(p => p.ConfigDirectory).OfType<string>(), d => Assert.True(Path.IsPathFullyQualified(d)));
    }

    [Fact]
    public void A_configuration_that_selects_scripted_with_no_profiles_holds_none()
    {
        using var temp = new TemporaryHome();
        Write(temp, """{ "executor": "scripted" }""");

        DaemonOptions options = NodeConfiguration.Load(temp.Home);

        Assert.IsType<ScriptedExecutor>(options.Executor);
        Assert.Empty(options.Profiles);
    }

    [Theory]
    [InlineData("{ \"executor\": \"scripted\", ", "is not valid JSON; the reader stopped at $ (line 1)")]
    [InlineData("", "is not valid JSON")]
    [InlineData("[]", "does not allow at $ (line 1)")]
    [InlineData("""{ "executor": "scripted", "gates": [] }""", "does not allow at $.gates (line 1): a property the format does not name")]
    [InlineData("""{ "executor": "scripted", "profiles": { "claude-code": [ { "name": "work", "configDirectory": "a", "apiKey": "k" } ] } }""", "at $.profiles.claude-code[0].apiKey")]
    [InlineData("""{ "executor": "scripted", "Executor": "scripted" }""", "at $.Executor")]
    [InlineData("""{ "executor": "scripted", "executor": "scripted" }""", "at $.executor (line 1)")]
    [InlineData("""{ "executor": "scripted", "profiles": { "claude-code": [], "claude-code": [] } }""", "at $.profiles.claude-code")]
    [InlineData("""{ "executor": 3 }""", "at $.executor (line 1)")]
    [InlineData("""{ "executor": "scripted", "profiles": [] }""", "at $.profiles")]
    [InlineData("""{ "executor": "scripted", "profiles": { "claude-code": [ { "name": "work", "configDirectory": "a\u0000b" } ] } }""", "gives profile 'work' under \"claude-code\" a \"configDirectory\" that is not a valid path")]
    [InlineData("""{ }""", "names no executor")]
    [InlineData("""null""", "names no executor")]
    [InlineData("""{ "executor": null }""", "names no executor")]
    [InlineData("""{ "executor": "  " }""", "names no executor")]
    [InlineData("""{ "executor": "claude" }""", "names the executor 'claude', which is not a key this daemon knows; the keys are \"claude-code\" and \"scripted\".")]
    [InlineData("""{ "executor": "scripted", "claudeExecutable": "claude" }""", "gives a \"claudeExecutable\" while the executor is \"scripted\"")]
    [InlineData("""{ "executor": "claude-code", "claudeExecutable": 3 }""", "at $.claudeExecutable")]
    [InlineData("""{ "executor": "claude-code", "claudeExecutable": "bin/a\u0000b" }""", "gives a \"claudeExecutable\" that is not a valid path")]
    [InlineData("""{ "executor": "Scripted" }""", "names the executor 'Scripted', which is not a key")]
    [InlineData("""{ "executor": "scripted", "profiles": { "codex": [ { "name": "work", "configDirectory": "a" } ] } }""", "under the vendor 'codex'")]
    [InlineData("""{ "executor": "scripted", "profiles": { "claude-code": [ { "name": " ", "configDirectory": "a" } ] } }""", "has a blank name on profile 1 under \"claude-code\"")]
    [InlineData("""{ "executor": "scripted", "profiles": { "claude-code": [ { "configDirectory": "a" } ] } }""", "has a blank name on profile 1 under \"claude-code\"")]
    [InlineData("""{ "executor": "scripted", "profiles": { "claude-code": [ null ] } }""", "has a blank name on profile 1 under \"claude-code\"")]
    [InlineData("""{ "executor": "scripted", "profiles": { "claude-code": [ { "name": "work", "configDirectory": "a" }, { "name": " work", "apiKeyHelper": "b" } ] } }""", "more than one profile named 'work' under \"claude-code\"; profile 2")]
    [InlineData("""{ "executor": "scripted", "profiles": { "claude-code": [ { "name": "work", "configDirectory": "a", "apiKeyHelper": "b" } ] } }""", "both a \"configDirectory\" and an \"apiKeyHelper\"")]
    [InlineData("""{ "executor": "scripted", "profiles": { "claude-code": [ { "name": "work" } ] } }""", "neither a \"configDirectory\" nor an \"apiKeyHelper\"")]
    [InlineData("""{ "executor": "scripted", "profiles": { "claude-code": [ { "name": "work", "configDirectory": " ", "apiKeyHelper": "" } ] } }""", "neither a \"configDirectory\" nor an \"apiKeyHelper\"")]
    public void A_configuration_the_documented_format_does_not_allow_is_refused_with_a_rule_naming_the_file_and_the_entry(string content, string rule)
    {
        using var temp = new TemporaryHome();
        Write(temp, content);

        DomainException exception = Assert.Throws<DomainException>(() => NodeConfiguration.Load(temp.Home));
        Assert.StartsWith($"The node configuration {temp.Home.ConfigurationPath} ", exception.Message, StringComparison.Ordinal);
        Assert.Contains(rule, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "executor": "scripted", "gates": [] }""")]
    [InlineData("""{ "executor": "scripted", "profiles": { "claude-code": [ { "name": "work", "x": 1 } ] } }""")]
    [InlineData("""{ "executor": 3 }""")]
    [InlineData("{\n  \"executor\": \"scripted\",\n}")]
    public void A_refused_format_names_its_location_once_and_no_type_of_the_daemon(string content)
    {
        using var temp = new TemporaryHome();
        Write(temp, content);

        string message = Assert.Throws<DomainException>(() => NodeConfiguration.Load(temp.Home)).Message;

        Assert.DoesNotContain("Coanda.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Shape", message, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("LineNumber", message, StringComparison.Ordinal);
        Assert.DoesNotContain("BytePositionInLine", message, StringComparison.Ordinal);
        // the location is named once
        Assert.Equal(2, message.Split("line ").Length);
    }

    [Fact]
    public void A_trailing_comma_is_located_on_its_one_based_line()
    {
        using var temp = new TemporaryHome();
        Write(temp, "{\n  \"executor\": \"scripted\",\n}");

        DomainException exception = Assert.Throws<DomainException>(() => NodeConfiguration.Load(temp.Home));
        Assert.EndsWith("is not valid JSON; the reader stopped at $ (line 3).", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configuration_path_that_is_a_directory_is_refused_rather_than_read_as_missing()
    {
        using var temp = new TemporaryHome();
        Directory.CreateDirectory(temp.Home.ConfigurationPath);

        DomainException exception = Assert.Throws<DomainException>(() => NodeConfiguration.Load(temp.Home));
        Assert.Equal($"The node configuration {temp.Home.ConfigurationPath} is a directory; the node configuration is a file.", exception.Message);
    }

    [Fact]
    public void A_configuration_the_daemon_cannot_read_is_refused_with_a_rule()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TemporaryHome();
        Write(temp, """{ "executor": "scripted" }""");
        File.SetUnixFileMode(temp.Home.ConfigurationPath, UnixFileMode.None);
        try
        {
            if (Readable(temp.Home.ConfigurationPath))
            {
                return; // A superuser reads it regardless of its mode, so there is nothing to refuse.
            }

            DomainException exception = Assert.Throws<DomainException>(() => NodeConfiguration.Load(temp.Home));
            Assert.StartsWith($"The node configuration {temp.Home.ConfigurationPath} could not be read: ", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(temp.Home.ConfigurationPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Profile_names_that_differ_only_in_case_are_two_profiles()
    {
        using var temp = new TemporaryHome();
        Write(temp, """{ "executor": "scripted", "profiles": { "claude-code": [ { "name": "work", "configDirectory": "/a" }, { "name": "Work", "configDirectory": "/b" } ] } }""");

        Assert.Equal(["work", "Work"], NodeConfiguration.Load(temp.Home).Profiles.Select(p => p.Name));
    }

    [Fact]
    public void Loading_reads_no_config_directory_and_checks_nothing_exists()
    {
        using var temp = new TemporaryHome();
        string missing = Path.Combine(temp.Home.Path, "no-such-directory");
        Write(temp, """{ "executor": "scripted", "profiles": { "claude-code": [ { "name": "work", "configDirectory": "no-such-directory" }, { "name": "ci", "apiKeyHelper": "/no/such/helper --print" } ] } }""");

        DaemonOptions options = NodeConfiguration.Load(temp.Home);

        Assert.Equal([missing, "/no/such/helper --print"], options.Profiles.Select(p => p.ConfigDirectory ?? p.ApiKeyHelper));
        Assert.False(Directory.Exists(missing));
    }

    private static bool Readable(string path)
    {
        try
        {
            using FileStream _ = File.OpenRead(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Write(TemporaryHome temp, string content) => File.WriteAllText(temp.Home.ConfigurationPath, content);
}
