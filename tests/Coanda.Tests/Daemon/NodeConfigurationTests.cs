using Coanda.Core;
using Coanda.Daemon.Api;
using Coanda.Daemon.Executors;

namespace Coanda.Tests.Daemon;

public sealed class NodeConfigurationTests
{
    [Fact]
    public void A_home_with_no_configuration_runs_the_scripted_executor_with_no_entries_and_holds_no_profiles()
    {
        using var temp = new TemporaryHome();

        DaemonOptions options = NodeConfiguration.Load(temp.Home);

        options.Should().BeEquivalentTo(new DaemonOptions(), "a missing file is what coa daemon run did before the file existed");
        options.Executor.Should().BeNull("the host then builds a scripted executor with no entries");
        options.Profiles.Should().BeEmpty();
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

        options.Executor.Should().BeOfType<ScriptedExecutor>();
        options.Profiles.Should().Equal(
            new CredentialProfile("claude-code", "work", Path.Combine(temp.Home.Path, "accounts", "work"), null),
            new CredentialProfile("claude-code", "personal", absolute, null),
            new CredentialProfile("claude-code", "ci", null, "op read op://ci/anthropic/key"));
        options.Profiles.Select(p => p.ConfigDirectory).OfType<string>().Should().OnlyContain(d => Path.IsPathFullyQualified(d));
    }

    [Fact]
    public void A_configuration_that_selects_scripted_with_no_profiles_holds_none()
    {
        using var temp = new TemporaryHome();
        Write(temp, """{ "executor": "scripted" }""");

        DaemonOptions options = NodeConfiguration.Load(temp.Home);

        options.Executor.Should().BeOfType<ScriptedExecutor>();
        options.Profiles.Should().BeEmpty();
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
    [InlineData("""{ "executor": "claude-code" }""", "names the executor 'claude-code', which is not a key")]
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

        FluentActions.Invoking(() => NodeConfiguration.Load(temp.Home))
            .Should().Throw<DomainException>()
            .Which.Message.Should().StartWith($"The node configuration {temp.Home.ConfigurationPath} ").And.Contain(rule);
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

        string message = FluentActions.Invoking(() => NodeConfiguration.Load(temp.Home))
            .Should().Throw<DomainException>().Which.Message;

        message.Should().NotContain("Coanda.").And.NotContain("Shape").And.NotContain("System.")
            .And.NotContain("LineNumber").And.NotContain("BytePositionInLine");
        message.Split("line ").Should().HaveCount(2, "the location is named once");
    }

    [Fact]
    public void A_trailing_comma_is_located_on_its_one_based_line()
    {
        using var temp = new TemporaryHome();
        Write(temp, "{\n  \"executor\": \"scripted\",\n}");

        FluentActions.Invoking(() => NodeConfiguration.Load(temp.Home))
            .Should().Throw<DomainException>().Which.Message.Should().EndWith("is not valid JSON; the reader stopped at $ (line 3).");
    }

    [Fact]
    public void A_configuration_path_that_is_a_directory_is_refused_rather_than_read_as_missing()
    {
        using var temp = new TemporaryHome();
        Directory.CreateDirectory(temp.Home.ConfigurationPath);

        FluentActions.Invoking(() => NodeConfiguration.Load(temp.Home))
            .Should().Throw<DomainException>()
            .WithMessage($"The node configuration {temp.Home.ConfigurationPath} is a directory; the node configuration is a file.");
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

            FluentActions.Invoking(() => NodeConfiguration.Load(temp.Home))
                .Should().Throw<DomainException>()
                .Which.Message.Should().StartWith($"The node configuration {temp.Home.ConfigurationPath} could not be read: ");
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

        NodeConfiguration.Load(temp.Home).Profiles.Select(p => p.Name).Should().Equal("work", "Work");
    }

    [Fact]
    public void Loading_reads_no_config_directory_and_checks_nothing_exists()
    {
        using var temp = new TemporaryHome();
        string missing = Path.Combine(temp.Home.Path, "no-such-directory");
        Write(temp, """{ "executor": "scripted", "profiles": { "claude-code": [ { "name": "work", "configDirectory": "no-such-directory" }, { "name": "ci", "apiKeyHelper": "/no/such/helper --print" } ] } }""");

        DaemonOptions options = NodeConfiguration.Load(temp.Home);

        options.Profiles.Select(p => p.ConfigDirectory ?? p.ApiKeyHelper).Should().Equal(missing, "/no/such/helper --print");
        Directory.Exists(missing).Should().BeFalse();
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
