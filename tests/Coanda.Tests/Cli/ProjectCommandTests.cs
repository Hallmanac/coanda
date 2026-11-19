using System.Text.RegularExpressions;
using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Modules.Software;
using Coanda.Tests.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Coanda.Tests.Cli;

/// <summary>
/// What <c>coa project add</c> sends: the project and its repo's remote, and each <c>--setting</c> as the
/// module setting it names, which <c>coa project module show</c> prints and <c>coa project show</c> and
/// <c>coa project list</c> do not. A setting the daemon refuses registers nothing and prints the daemon's
/// reason, and an option the command cannot read is refused before anything is sent.
/// </summary>
public sealed partial class ProjectCommandTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public void Each_setting_is_sent_by_module_and_setting_name_its_value_read_as_json_when_it_is_json_and_as_text_otherwise()
    {
        ProjectAddRequest request = new ProjectAddSettings
        {
            Name = "custom",
            Repo = "git@example.invalid:team/Custom.git",
            Settings = ["mod/branches={\"custom\":\"trunk\"}", "mod/build=make build", "other/count=3", "mod/eq=a=b", "mod/path=x/y"],
        }.ToRequest();

        Assert.Equal(("custom", "git@example.invalid:team/Custom.git"), (request.Name, request.Remote));
        Assert.Equal(
            [
                ("mod", "branches", """{"custom":"trunk"}"""),
                ("mod", "build", "\"make build\""),
                ("other", "count", "3"),
                ("mod", "eq", "\"a=b\""),
                ("mod", "path", "\"x/y\""),
            ],
            (request.Settings ?? []).Select(s => (s.Module, s.Name, s.Value.GetRawText())));
    }

    [Fact]
    public void A_setting_value_is_read_the_way_coa_project_module_set_reads_one()
    {
        foreach (string value in new[] { "true", "3", """["a"]""", "make build", "", "{not json" })
        {
            ModuleSettingValue? parsed = ModuleSettingOption.Parse($"mod/name={value}");
            var set = new ProjectModuleSetSettings { Project = "p", Module = "mod", Name = "name", Value = value };

            Assert.Equal(set.ToValue().GetRawText(), parsed?.Value.GetRawText());
        }
    }

    [Fact]
    public void Without_a_setting_nothing_but_the_project_and_its_remote_is_sent()
    {
        var settings = new ProjectAddSettings { Name = "plain", Repo = "git@example.invalid:plain.git" };

        Assert.True(settings.Validate().Successful);
        Assert.Empty(settings.ToRequest().Settings ?? []);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("build=make")]
    [InlineData("/build=make")]
    [InlineData("mod/=make")]
    [InlineData(" /build=make")]
    [InlineData("mod/ =make")]
    [InlineData("mod/build")]
    [InlineData("=make")]
    public void A_setting_the_command_cannot_read_as_a_module_a_name_and_a_value_is_refused(string option)
    {
        Assert.Null(ModuleSettingOption.Parse(option));
        Assert.False(new ProjectAddSettings { Name = "bad", Repo = "git@example.invalid:x.git", Settings = ["mod/fine=1", option] }.Validate().Successful);
    }

    [Fact]
    public void Leaving_the_repo_out_is_valid_and_sends_no_remote()
    {
        var settings = new ProjectAddSettings { Name = "norepo" };

        Assert.True(settings.Validate().Successful);
        Assert.Null(settings.ToRequest().Remote);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_repo_given_with_an_empty_or_blank_remote_is_refused(string remote)
    {
        ValidationResult result = new ProjectAddSettings { Name = "blank", Repo = remote }.Validate();

        Assert.False(result.Successful);
        Assert.Equal(
            "--repo was given with no remote. Name the remote of the project's repo with --repo <url>, or leave --repo out to register the project with no repos.",
            result.Message);
    }

    [Fact]
    public void A_project_with_no_repos_is_registered_shown_and_listed_as_having_none()
    {
        var none = new ProjectView("bare", [], "owner", DateTimeOffset.UnixEpoch, null, ["software"]);
        var other = new ProjectView("alpha-project", [new("a", "git@example.invalid:a.git")], "owner", DateTimeOffset.UnixEpoch, null, []);

        Assert.Equal(
            "Registered project bare with no repos; switched on for it: module software, whose settings coa project module show bare software shows.",
            ProjectAddCommand.RegisteredLine(none));
        Assert.Equal(
            "Registered project bare with no repos; no module is switched on for it.",
            ProjectAddCommand.RegisteredLine(none with { Modules = [] }));

        IReadOnlyList<(string Key, string Value)> rows = ProjectShowCommand.ShowRows(none);
        Assert.Equal(["Name", "Repo", "Profile", "Registered"], rows.Select(r => r.Key));
        Assert.Equal(("Repo", "none; with no repo to carry its ledger, its parcels cannot be claimed"), rows[1]);

        Assert.Equal(["bare  no repos"], ProjectListCommand.ListLines([none]));
        Assert.Equal(["bare  no repos", "zed   no repos"], ProjectListCommand.ListLines([none, none with { Name = "zed" }]));
        Assert.Equal(
            [
                "alpha-project  a  git@example.invalid:a.git",
                "bare           no repos",
            ],
            ProjectListCommand.ListLines([other, none]));
    }

    [Fact]
    public async Task The_add_command_given_no_repo_registers_the_project_with_no_repos()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);

        Ran added = await RunAsync(temp.Home, "project", "add", "bare");
        Ran listed = await RunAsync(temp.Home, "project", "list");
        Ran again = await RunAsync(temp.Home, "project", "add", "bare");

        Assert.True(added.ExitCode == 0, added.Output + added.Error);
        Assert.Equal(
            Squashed("Registered project bare with no repos; switched on for it: module software, whose settings coa project module show bare software shows."),
            Squashed(added.Output));
        Assert.True(listed.ExitCode == 0, listed.Output + listed.Error);
        Assert.Equal(Squashed("bare  no repos"), Squashed(listed.Output));
        Assert.NotEqual(0, again.ExitCode);
        Assert.Contains(Squashed("Project 'bare' is already registered, with no repos."), Squashed(again.Output + again.Error), StringComparison.Ordinal);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_claim_on_a_parcel_of_a_project_with_no_repos_is_refused_by_the_api_and_the_claim_command()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await client.PostAsync<ProjectView>("/projects", new ProjectAddSettings { Name = "bare" }.ToRequest(), None);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest("bare", "task", "Unclaimable", null), None);
        string rule = $"Parcel {Ids.Short(parcel.Id)} is not claimed: project 'bare' has no repo to carry its ledger, so a claim on its parcels can never be held.";

        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => client.PostAsync<ParcelClaimResult>("/parcels/claim", new ParcelClaimRequest("bare", parcel.Id), None));
        Ran claimed = await RunAsync(temp.Home, "parcel", "claim", parcel.Id.ToString(), "--project", "bare");

        Assert.Equal(rule, refused.Message);
        Assert.NotEqual(0, claimed.ExitCode);
        Assert.Contains(Squashed(rule), Squashed(claimed.Output + claimed.Error), StringComparison.Ordinal);
        Coanda.Core.Parcels.Parcel stored = await daemon.Services.GetRequiredService<ParcelService>().ShowAsync(Principal.LocalOwner, "bare", parcel.Id, None)
            ?? throw new InvalidOperationException("The parcel was not stored.");
        Assert.Empty(stored.Claimants);

        await daemon.StopAsync(None);
    }

    [Fact]
    public void The_registered_line_names_each_switched_on_module_and_the_command_that_shows_its_settings_or_says_none_is()
    {
        var two = new ProjectView("p", [new("p", "git@example.invalid:p.git")], "owner", DateTimeOffset.UnixEpoch, null, ["cms", "software"]);
        var none = two with { Modules = [] };

        Assert.Equal(
            "Registered project p with repo p (git@example.invalid:p.git); switched on for it: module cms, whose settings coa project module show p cms shows; module software, whose settings coa project module show p software shows.",
            ProjectAddCommand.RegisteredLine(two));
        Assert.Equal(
            "Registered project p with repo p (git@example.invalid:p.git); no module is switched on for it.",
            ProjectAddCommand.RegisteredLine(none));
    }

    [Fact]
    public async Task The_add_command_offers_only_its_remote_and_settings_options()
    {
        using var temp = new TemporaryHome();

        Ran help = await RunAsync(temp.Home, "project", "add", "--help");

        Assert.Equal(0, help.ExitCode);
        Assert.Equal(
            ["--help", "--repo", "--setting"],
            OptionNames().Matches(help.Output[help.Output.IndexOf("OPTIONS:", StringComparison.Ordinal)..]).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_setting_given_at_registration_is_recorded_in_its_save_and_module_show_reports_it()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);

        Ran added = await RunAsync(temp.Home, "project", "add", "custom", "--repo", "git@example.invalid:custom.git", "--setting", "software/build=make build [fast]");
        Ran module = await RunAsync(temp.Home, "project", "module", "show", "custom", "software");

        Assert.True(added.ExitCode == 0, added.Output + added.Error);
        Assert.Equal(
            Squashed("Registered project custom with repo custom (git@example.invalid:custom.git); switched on for it: module software, whose settings coa project module show custom software shows."),
            Squashed(added.Output));
        Assert.Equal(0, module.ExitCode);
        Assert.Contains(Squashed("\"make build [fast]\""), Squashed(module.Output), StringComparison.Ordinal);

        // add was the only command that wrote, so the value module show reports is the one its registration recorded
        ModuleSettingsShown shown = await daemon.Services.GetRequiredService<ModuleSettingsService>().ShowAsync(Principal.LocalOwner, "custom", SoftwareSteps.Module, None);
        ReplicatedSettingShown build = Assert.Single(shown.Replicated, s => s.Name == SoftwareSettings.Build);
        Assert.Equal((SettingSource.Set, "\"make build [fast]\""), (build.Source, build.Value?.GetRawText()));
        Assert.Null(Assert.Single(shown.Replicated, s => s.Name == SoftwareSettings.PrimaryBranch).Value);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_project_shows_and_lists_its_repo_and_no_setting_while_module_show_prints_the_settings()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        var settings = new ProjectAddSettings
        {
            Name = "custom", Repo = "git@example.invalid:custom.git", Settings = ["software/primary-branch={\"custom\":\"main\"}", "software/build=make build [fast]"],
        };

        ProjectView added = await client.PostAsync<ProjectView>("/projects", settings.ToRequest(), None);
        ProjectView shown = await client.GetAsync<ProjectView>(ProjectEndpoints.PathOf("custom"), None);
        ModuleSettingsShown module = await client.GetAsync<ModuleSettingsShown>(ProjectModuleEndpoints.ShowPathOf("custom", SoftwareSteps.Module), None);

        Assert.Equal([SoftwareSteps.Module], added.Modules);
        Assert.Equal([SoftwareSteps.Module], shown.Modules);
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
        var view = new ProjectView("multi", [new("one", "git@example.invalid:one.git"), new("two", "git@example.invalid:two.git")], "owner", DateTimeOffset.UnixEpoch, null, []);
        var single = new ProjectView("a", [new("a-repo", "git@example.invalid:a.git")], "owner", DateTimeOffset.UnixEpoch, null, []);

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
        // read by the command, and refused before anything is sent
        { ["project", "add", "refused", "--repo", "git@example.invalid:refused.git", "--setting", "build=make"], "Give each setting as --setting <module>/<name>=<value>; 'build=make' is not in that form." },
        { ["project", "add", "refused", "--repo", "git@example.invalid:refused.git", "--setting", "software/build"], "Give each setting as --setting <module>/<name>=<value>; 'software/build' is not in that form." },
        // refused by the daemon, whose reason the command prints
        {
            ["project", "add", "refused", "--repo", "git@example.invalid:refused.git", "--setting", "software/branch=main"],
            $"Module 'software' declares no replicated setting named 'branch'; it declares {string.Join(", ", SoftwareDescribe.Settings.Replicated.Select(r => $"'{r.Name}'"))}"
        },
        { ["project", "add", "refused", "--repo", "git@example.invalid:refused.git", "--setting", "cms/title=x"], "was given a setting of module 'cms', which its registration does not switch on" },
        { ["project", "add", "refused", "--repo", "git@example.invalid:refused.git", "--setting", "software/build= "], "setting 'build'" },
        { ["project", "add", "refused", "--repo", "git@example.invalid:refused.git", "--setting", "software/test="], "setting 'test'" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_setting_the_command_cannot_read_or_the_daemon_refuses_registers_nothing_and_says_why(string[] arguments, string named)
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
            ["project", "add", "try-coanda", "--repo", "git@github.com:you/try-coanda.git", "--setting", "software/build=dotnet build --configuration Release", "--setting", """software/primary-branch={"try-coanda":"trunk"}"""],
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
                (SoftwareSettings.PrimaryBranch, """{"try-coanda":"trunk"}"""),
                (SoftwareSettings.Build, "\"dotnet build --configuration Release\""),
                (SoftwareSettings.Test, "\"dotnet test --no-build\""),
                (SoftwareSettings.RequiredChecks, """["build-test (ubuntu-latest)","build-test (windows-latest)"]"""),
            ],
            settings.Replicated.Select(s => (s.Name, s.Value?.GetRawText())));
        Assert.Contains(Squashed("dotnet test --no-build"), outputs[3], StringComparison.Ordinal);
        Assert.Contains(Squashed("try-coanda git@github.com:you/try-coanda.git"), outputs[4], StringComparison.Ordinal);
        Assert.DoesNotContain("trunk", outputs[4], StringComparison.Ordinal);
        Assert.Equal(Squashed("try-coanda  try-coanda  git@github.com:you/try-coanda.git"), outputs[5]);

        await daemon.StopAsync(None);
    }

    /// <summary>The text with every escape sequence and all white space removed, since the CLI wraps and may colour what it prints.</summary>
    private static string Squashed(string text) => Whitespace().Replace(Escapes().Replace(text, string.Empty), string.Empty);

    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex Escapes();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<=\s)--[a-z][a-z-]*")]
    private static partial Regex OptionNames();

    /// <summary>Runs the built <c>coa</c> against <paramref name="home"/>, whose daemon the test started, until it exits.</summary>
    private static Task<Ran> RunAsync(NodeHome home, params string[] arguments) => CoaProcess.RunAsync(home, None, arguments);
}
