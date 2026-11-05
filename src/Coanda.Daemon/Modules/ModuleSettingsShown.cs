using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coanda.Daemon.Modules;

/// <summary>Whether a module switched on for a project is configured on this node, or on and unconfigured.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModuleState>))]
public enum ModuleState
{
    Configured,
    Unconfigured,
}

/// <summary>
/// A module's state in a project on this node, and the names of the settings it requires that have no
/// value and no default, which are none exactly when it is configured.
/// </summary>
public sealed record ModuleConfiguration(ModuleState State, IReadOnlyList<string> Missing);

/// <summary>Where a shown setting's value comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingSource>))]
public enum SettingSource
{
    /// <summary>The project set it, or for the credential profile setting, this node selected a profile it still holds.</summary>
    Set,

    /// <summary>Nothing was set and the setting's default stands, which for the credential profile setting is the node's default identity.</summary>
    Default,

    /// <summary>Nothing was set and the setting has no default.</summary>
    Unset,

    /// <summary>For the credential profile setting alone: this node selected a profile it no longer holds, which is no value, and no default stands in for it.</summary>
    Missing,
}

/// <summary>One replicated setting a module declares, with the value it has in the project and where that comes from.</summary>
public sealed record ReplicatedSettingShown(string Name, bool Required, SettingSource Source, JsonElement? Value);

/// <summary>
/// The credential profile setting of a module, in a project on this node: the profile selected, or null
/// when none is; where the setting's value comes from; and the profiles the node holds for the module.
/// </summary>
public sealed record CredentialProfileShown(bool Required, SettingSource Source, string? Profile, IReadOnlyList<string> Held);

/// <summary>A module's settings in a project, as show renders them, with its configured state on this node.</summary>
public sealed record ModuleSettingsShown(
    string Project,
    string Module,
    IReadOnlyList<ReplicatedSettingShown> Replicated,
    CredentialProfileShown? CredentialProfile,
    ModuleConfiguration Configuration);
