using System.Text.Json.Serialization;
using Coanda.Core.Modules.Events;

namespace Coanda.Core.Modules;

/// <summary>
/// A module's replicated settings in one project: each setting given a value, by name, with its value
/// as JSON text. It lives in the project's own tenant, one per project and module. State changes only
/// through <c>Apply</c>; a later value for a name replaces the earlier one. The store snapshots this
/// class inline.
/// </summary>
public sealed class ModuleSettings
{
    [JsonInclude]
    public Guid Id { get; private set; }
    [JsonInclude]
    public string Project { get; private set; } = string.Empty;
    [JsonInclude]
    public string Module { get; private set; } = string.Empty;

    /// <summary>The value of each setting given one, as JSON text, by the setting's name.</summary>
    [JsonInclude]
    public IReadOnlyDictionary<string, string> Values { get; private set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonInclude]
    public DateTimeOffset UpdatedAt { get; private set; }

    public static ModuleSettings Create(ModuleSettingSet set)
    {
        var settings = new ModuleSettings();
        settings.Apply(set);
        return settings;
    }

    public void Apply(ModuleSettingSet set)
    {
        Id = set.SettingsId;
        Project = set.Project;
        Module = set.Module;
        Values = new Dictionary<string, string>(Values, StringComparer.Ordinal) { [set.Name] = set.Value };
        UpdatedAt = set.SetAt;
    }

    /// <summary>The value given to the setting <paramref name="name"/>, as JSON text, or null when none was.</summary>
    public string? ValueOf(string name) => Values.TryGetValue(name, out string? value) ? value : null;
}
