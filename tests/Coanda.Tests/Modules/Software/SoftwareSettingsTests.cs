using System.Text.Json;
using Coanda.Cli.Commands;
using Coanda.Core.Modules;
using Coanda.Core.Registry;
using Coanda.Core;
using Coanda.Daemon.Modules;
using Coanda.Modules.Software;
using Coanda.Tests.Daemon;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Modules.Software;

/// <summary>
/// The software module's settings in a project, set through the generic module settings commands: the
/// primary branch per repo, the build and test commands with their defaults, and the required checks,
/// which are none until set. A blank command or check name is refused as the project's record refused it
/// before the settings moved to the module.
/// </summary>
public sealed class SoftwareSettingsTests
{
    private const string Ubuntu = "build-test (ubuntu-latest)";
    private const string Windows = "build-test (windows-latest)";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal Owner = Principal.LocalOwner;

    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    private static async Task<ModuleSettingsShown> ShowAsync(SoftwareNode node, string project) =>
        await node.Services.GetRequiredService<ModuleSettingsService>().ShowAsync(Owner, project, SoftwareSteps.Module, None);

    private static Task<ModuleSettingsShown> SetAsync(SoftwareNode node, string project, string name, string value) =>
        node.Services.GetRequiredService<ModuleSettingsService>().SetAsync(Owner, project, SoftwareSteps.Module, name, Json(value), None);

    private static async Task<IReadOnlyList<ModuleSettings>> RecordedAsync(SoftwareNode node, string project)
    {
        await using IQuerySession session = node.Store.QuerySession(project);
        return await session.Query<ModuleSettings>().ToListAsync(None);
    }

    [Fact]
    public async Task A_project_registered_with_the_branch_alone_runs_the_default_commands_and_names_no_check()
    {
        await using SoftwareNode node = await SoftwareNode.StartAsync();
        await node.Projects.AddAsync(Owner, "beta", RegisteredProjects.Remote, RegisteredProjects.BranchOnly, None);

        ModuleSettingsShown shown = await ShowAsync(node, "beta");

        Assert.Equal(
            [
                (SoftwareSettings.PrimaryBranch, SettingSource.Set, """{"repo":"main"}"""),
                (SoftwareSettings.Build, SettingSource.Default, "\"dotnet build\""),
                (SoftwareSettings.Test, SettingSource.Default, "\"dotnet test\""),
                (SoftwareSettings.RequiredChecks, SettingSource.Unset, "null"),
            ],
            shown.Replicated.Select(s => (s.Name, s.Source, s.Value?.GetRawText() ?? "null")));
        Assert.Equal(ModuleState.Configured, shown.Configuration.State);
    }

    [Fact]
    public async Task The_required_checks_are_set_replaced_and_shown_exactly_as_given()
    {
        await using SoftwareNode node = await SoftwareNode.StartAsync();
        await node.Projects.AddAsync(Owner, "team/coanda", RegisteredProjects.Remote, RegisteredProjects.BranchOnly, None);

        await SetAsync(node, "team/coanda", SoftwareSettings.RequiredChecks, $"""["{Ubuntu}", "{Windows}"]""");
        ModuleSettingsShown shown = await SetAsync(node, "team/coanda", SoftwareSettings.RequiredChecks, """[" lint [fast] "]""");

        Assert.Equal("""[" lint [fast] "]""", shown.Replicated.Single(s => s.Name == SoftwareSettings.RequiredChecks).Value?.GetRawText());
        // The checks are printed as text, not markup.
        Assert.Contains((SoftwareSettings.RequiredChecks, """[[" lint [[fast]] "]]"""), ProjectModuleShowCommand.ShowRows(shown));
    }

    public static TheoryData<string, string, string> Blank => new()
    {
        { SoftwareSettings.Build, "\"\"", "PatternMismatch" },
        { SoftwareSettings.Build, "\"   \"", "PatternMismatch" },
        { SoftwareSettings.Test, "\" \"", "PatternMismatch" },
        { SoftwareSettings.RequiredChecks, $"""["{Ubuntu}", " "]""", "ArrayItemNotValid" },
        { SoftwareSettings.RequiredChecks, "[]", "TooFewItems" },
        { SoftwareSettings.PrimaryBranch, """{"repo":" "}""", "AdditionalPropertiesNotValid" },
        { SoftwareSettings.PrimaryBranch, "{}", "TooFewProperties" },
        { SoftwareSettings.PrimaryBranch, "\"main\"", "ObjectExpected" },
    };

    [Theory]
    [MemberData(nameof(Blank))]
    public async Task A_blank_command_check_or_branch_is_refused_with_nothing_recorded(string name, string value, string failure)
    {
        await using SoftwareNode node = await SoftwareNode.StartAsync();
        await node.Projects.AddAsync(Owner, "beta", RegisteredProjects.Remote, [], None);

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => SetAsync(node, "beta", name, value));

        Assert.StartsWith($"The value given for setting '{name}' of module 'software' does not meet the setting's schema: {failure}", refused.Message, StringComparison.Ordinal);
        Assert.Empty(await RecordedAsync(node, "beta"));
    }
}
