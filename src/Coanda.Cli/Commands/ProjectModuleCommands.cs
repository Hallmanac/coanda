using System.ComponentModel;
using System.Text.Json;
using Coanda.Daemon;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Modules;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coanda.Cli.Commands;

public class ProjectModuleSettings : CommandSettings
{
    [CommandArgument(0, "<PROJECT>")]
    [Description("The project's name.")]
    public required string Project { get; init; }

    [CommandArgument(1, "<MODULE>")]
    [Description("The module's name, one switched on for the project.")]
    public required string Module { get; init; }
}

public sealed class ProjectModuleSetSettings : ProjectModuleSettings
{
    [CommandArgument(2, "<NAME>")]
    [Description("The name of a replicated setting the module declares.")]
    public required string Name { get; init; }

    [CommandArgument(3, "<VALUE>")]
    [Description("The setting's value: read as JSON when it is JSON, such as true, 3, or [\"a\"], and as text otherwise.")]
    public required string Value { get; init; }

    /// <summary>The value as JSON when it parses as JSON, and as a JSON string holding it otherwise.</summary>
    public JsonElement ToValue()
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(Value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(Value);
        }
    }
}

public sealed class ProjectModuleSelectProfileSettings : ProjectModuleSettings
{
    [CommandArgument(2, "<PROFILE>")]
    [Description("The name of a credential profile this node holds for the module: an entry directly inside the module's area of the node home.")]
    public required string Profile { get; init; }
}

public sealed class ProjectModuleSetCommand : AsyncCommand<ProjectModuleSetSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProjectModuleSetSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        ModuleSettingsShown shown = await client.PostAsync<ModuleSettingsShown>(
            $"{ProjectModuleEndpoints.Path}/settings",
            new ProjectModuleSetRequest(settings.Project, settings.Module, settings.Name, settings.ToValue()),
            cancellationToken);
        ProjectModuleShowCommand.Write(shown);
        return 0;
    }
}

public sealed class ProjectModuleSelectProfileCommand : AsyncCommand<ProjectModuleSelectProfileSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProjectModuleSelectProfileSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        ModuleSettingsShown shown = await client.PostAsync<ModuleSettingsShown>(
            $"{ProjectModuleEndpoints.Path}/profile",
            new ProjectModuleSelectProfileRequest(settings.Project, settings.Module, settings.Profile),
            cancellationToken);
        ProjectModuleShowCommand.Write(shown);
        return 0;
    }
}

public sealed class ProjectModuleShowCommand : AsyncCommand<ProjectModuleSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProjectModuleSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        Write(await client.GetAsync<ModuleSettingsShown>(ProjectModuleEndpoints.ShowPathOf(settings.Project, settings.Module), cancellationToken));
        return 0;
    }

    public static void Write(ModuleSettingsShown shown)
    {
        var table = new Table().HideHeaders().AddColumns("k", "v");
        foreach ((string key, string value) in ShowRows(shown))
        {
            table.AddRow(key, value);
        }

        AnsiConsole.Write(table);
    }

    /// <summary>
    /// The rows <c>coa project module show</c> prints, as markup with the settings' own text escaped:
    /// each replicated setting with its value, its default marked as the default, or unset; the
    /// credential profile setting; and the module's state on this node.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> ShowRows(ModuleSettingsShown shown) =>
    [
        ("Project", Markup.Escape(shown.Project)),
        ("Module", Markup.Escape(shown.Module)),
        .. shown.Replicated.Select(s => (Markup.Escape(s.Name), Required(s.Required, s.Source switch
        {
            SettingSource.Set => Json(s.Value),
            SettingSource.Default => $"{Json(s.Value)} (the default)",
            _ => "unset",
        }))),
        .. shown.CredentialProfile is { } profile ? [(ModuleSettingsService.CredentialProfileName, Required(profile.Required, Profile(profile)))] : Array.Empty<(string, string)>(),
        ("State", shown.Configuration.State == ModuleState.Configured
            ? "configured on this node"
            : $"on and unconfigured on this node; needs {Markup.Escape(string.Join(", ", shown.Configuration.Missing))}"),
    ];

    private static string Profile(CredentialProfileShown profile) => profile.Source switch
    {
        SettingSource.Set => Markup.Escape(profile.Profile ?? string.Empty),
        SettingSource.Missing => $"{Markup.Escape(profile.Profile ?? string.Empty)}, which this node no longer holds",
        SettingSource.Default => "the node's default identity (the default)",
        _ => "none",
    };

    private static string Required(bool required, string value) => required ? $"{value}; required" : value;

    private static string Json(JsonElement? value) => Markup.Escape(value is { } element ? element.GetRawText() : "null");
}
