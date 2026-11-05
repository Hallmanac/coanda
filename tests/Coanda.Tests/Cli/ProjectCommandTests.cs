using System.Diagnostics;
using System.Text.RegularExpressions;
using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Modules;
using Coanda.Modules.Software;
using Coanda.Tests.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace Coanda.Tests.Cli;

/// <summary>
/// What <c>coa project add</c> sends: the project and its repo's remote, and its <c>--branch</c>,
/// <c>--build</c>, and <c>--test</c> options as the software module's settings, which
/// <c>coa project module show</c> prints and <c>coa project show</c> and <c>coa project list</c> do not.
/// A command refused for a missing branch or a blank command records nothing.
/// </summary>
public sealed partial class ProjectCommandTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);
    private static readonly string Coa = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "coa.exe" : "coa");

    [Fact]
    public void The_options_are_sent_as_the_software_modules_settings_by_module_and_setting_name()
    {
        ProjectAddRequest request = new ProjectAddSettings
        {
            Name = "custom", Repo = "git@example.invalid:team/Custom.git", Branch = "trunk", Build = "make build", Test = "make test",
        }.ToRequest();

        Assert.Equal(("custom", "git@example.invalid:team/Custom.git"), (request.Name, request.Remote));
        Assert.Equal(
            [
                (SoftwareSteps.Module, SoftwareSettings.PrimaryBranch, """{"custom":"trunk"}"""),
                (SoftwareSteps.Module, SoftwareSettings.Build, "\"make build\""),
                (SoftwareSteps.Module, SoftwareSettings.Test, "\"make test\""),
            ],
            (request.Settings ?? []).Select(s => (s.Module, s.Name, s.Value.GetRawText())));
    }

    [Fact]
    public void Without_build_or_test_only_the_branch_is_sent_so_the_modules_defaults_stand()
    {
        ProjectAddRequest request = new ProjectAddSettings { Name = "plain", Repo = "git@example.invalid:plain.git", Branch = "main" }.ToRequest();

        Assert.Equal([SoftwareSettings.PrimaryBranch], (request.Settings ?? []).Select(s => s.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void A_command_without_a_branch_is_refused(string? branch)
    {
        Assert.False(new ProjectAddSettings { Name = "nobranch", Repo = "git@example.invalid:x.git", Branch = branch }.Validate().Successful);
        Assert.False(new ProjectAddSettings { Name = "norepo", Branch = "main" }.Validate().Successful);
        Assert.True(new ProjectAddSettings { Name = "fine", Repo = "git@example.invalid:x.git", Branch = "main" }.Validate().Successful);
    }

    [Fact]
    public async Task The_project_shows_and_lists_its_repo_and_no_setting_while_module_show_prints_the_settings()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        var settings = new ProjectAddSettings { Name = "custom", Repo = "git@example.invalid:custom.git", Branch = "main", Build = "make build [fast]" };

        await client.PostAsync<ProjectView>("/projects", settings.ToRequest(), None);
        ProjectView shown = await client.GetAsync<ProjectView>(ProjectEndpoints.PathOf("custom"), None);
        ModuleSettingsShown module = await client.GetAsync<ModuleSettingsShown>(ProjectModuleEndpoints.ShowPathOf("custom", SoftwareSteps.Module), None);

        Assert.Equal(["Name", "Repo", "Profile", "Registered"], ProjectShowCommand.ShowRows(shown).Select(r => r.Key));
        Assert.Contains(("Repo", "custom  git@example.invalid:custom.git"), ProjectShowCommand.ShowRows(shown));
        Assert.Equal(["custom  custom  git@example.invalid:custom.git"], ProjectListCommand.ListLines([shown]));
        IReadOnlyList<(string Key, string Value)> rows = ProjectModuleShowCommand.ShowRows(module);
        Assert.Contains((SoftwareSettings.PrimaryBranch, """{"custom":"main"}"""), rows);
        // The command is printed as text, not markup.
        Assert.Contains((SoftwareSettings.Build, "\"make build [[fast]]\""), rows);
        Assert.Contains((SoftwareSettings.Test, "\"dotnet test\" (the default)"), rows);

        await daemon.StopAsync(None);
    }

    [Fact]
    public void A_project_with_two_repos_lists_the_second_on_a_line_of_its_own()
    {
        var view = new ProjectView("multi", [new("one", "git@example.invalid:one.git"), new("two", "git@example.invalid:two.git")], "owner", DateTimeOffset.UnixEpoch, null);
        var single = new ProjectView("a", [new("a-repo", "git@example.invalid:a.git")], "owner", DateTimeOffset.UnixEpoch, null);

        Assert.Equal(
            [
                "a      a-repo  git@example.invalid:a.git",
                "multi  one     git@example.invalid:one.git",
                "       two     git@example.invalid:two.git",
            ],
            ProjectListCommand.ListLines([single, view]));
    }

    public static TheoryData<string[], string> Refused => new()
    {
        { ["project", "add", "refused", "--repo", "git@example.invalid:refused.git"], "--branch" },
        { ["project", "add", "refused", "--repo", "git@example.invalid:refused.git", "--branch", "main", "--build", " "], "setting 'build'" },
        { ["project", "add", "refused", "--repo", "git@example.invalid:refused.git", "--branch", "main", "--test", ""], "setting 'test'" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_command_without_a_branch_or_with_a_blank_command_is_refused_and_no_project_is_registered(string[] arguments, string named)
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);

        Ran refused = await RunAsync(temp.Home, arguments);
        Ran listed = await RunAsync(temp.Home, "project", "list");

        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains(Squashed(named), Squashed(refused.Output + refused.Error), StringComparison.Ordinal);
        Assert.Empty(await client.GetAsync<List<ProjectView>>("/projects", None));
        Assert.Equal((0, Squashed("No projects are registered on this node; coa project add registers one.")), (listed.ExitCode, Squashed(listed.Output)));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_commands_the_docs_show_run_as_written()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);

        string[][] commands =
        [
            ["project", "add", "try-coanda", "--repo", "git@github.com:you/try-coanda.git", "--branch", "main"],
            ["project", "module", "set", "try-coanda", "software", "test", "dotnet test --no-build"],
            ["project", "module", "set", "try-coanda", "software", "required-checks", """["build-test (ubuntu-latest)", "build-test (windows-latest)"]"""],
            ["project", "module", "show", "try-coanda", "software"],
            ["project", "show", "try-coanda"],
            ["project", "list"],
            ["parcel", "add", "--project", "try-coanda", "--type", "task", "--title", "Try Coanda"],
            ["parcel", "list", "--project", "try-coanda"],
        ];
        List<string> outputs = [];
        foreach (string[] command in commands)
        {
            Ran ran = await RunAsync(temp.Home, command);
            Assert.True(ran.ExitCode == 0, $"coa {string.Join(' ', command)} exited with {ran.ExitCode}: {ran.Output}{ran.Error}");
            outputs.Add(Squashed(ran.Output));
        }

        ModuleSettingsShown settings = await daemon.Services.GetRequiredService<ModuleSettingsService>().ShowAsync(
            Principal.LocalOwner, "try-coanda", SoftwareSteps.Module, None);
        Assert.Equal(
            [
                (SoftwareSettings.PrimaryBranch, """{"try-coanda":"main"}"""),
                (SoftwareSettings.Build, "\"dotnet build\""),
                (SoftwareSettings.Test, "\"dotnet test --no-build\""),
                (SoftwareSettings.RequiredChecks, """["build-test (ubuntu-latest)","build-test (windows-latest)"]"""),
            ],
            settings.Replicated.Select(s => (s.Name, s.Value?.GetRawText())));
        Assert.Contains(Squashed("dotnet test --no-build"), outputs[3], StringComparison.Ordinal);
        Assert.Contains(Squashed("try-coanda git@github.com:you/try-coanda.git"), outputs[4], StringComparison.Ordinal);
        Assert.DoesNotContain("main", outputs[4], StringComparison.Ordinal);
        Assert.Equal(Squashed("try-coanda  try-coanda  git@github.com:you/try-coanda.git"), outputs[5]);

        await daemon.StopAsync(None);
    }

    /// <summary>The text with every escape sequence and all white space removed, since the CLI wraps and may colour what it prints.</summary>
    private static string Squashed(string text) => Whitespace().Replace(Escapes().Replace(text, string.Empty), string.Empty);

    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex Escapes();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed record Ran(int ExitCode, string Output, string Error);

    /// <summary>Runs the built <c>coa</c> against <paramref name="home"/>, whose daemon the test started, until it exits.</summary>
    private static async Task<Ran> RunAsync(NodeHome home, params string[] arguments)
    {
        var start = new ProcessStartInfo(Coa) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[NodeHome.EnvironmentVariable] = home.Path;
        start.Environment.Remove(SessionAccess.TokenVariable);
        start.Environment.Remove(SessionAccess.HomeVariable);
        start.Environment.Remove("COANDA_PROJECT");

        using Process process = Process.Start(start) ?? throw new XunitException($"{Coa} did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(None);
        Task<string> error = process.StandardError.ReadToEndAsync(None);
        try
        {
            await process.WaitForExitAsync(None).WaitAsync(Bound);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw new XunitException($"coa {string.Join(' ', arguments)} did not return within {Bound.TotalSeconds:0} seconds.");
        }

        return new Ran(process.ExitCode, await output.WaitAsync(Bound), await error.WaitAsync(Bound));
    }
}
