using System.Text.Json.Serialization;
using Coanda.Core.Modules.Events;

namespace Coanda.Core.Modules;

/// <summary>
/// The credential profile selected for a module in one project on this node: a node-local setting, one
/// per project and module, kept in the node tenant. It names the profile and holds nothing of it. State
/// changes only through <c>Apply</c>. The store snapshots this class inline.
/// </summary>
public sealed class ModuleProfileSelection
{
    [JsonInclude]
    public Guid Id { get; private set; }
    [JsonInclude]
    public string Project { get; private set; } = string.Empty;
    [JsonInclude]
    public string Module { get; private set; } = string.Empty;
    [JsonInclude]
    public string Profile { get; private set; } = string.Empty;
    [JsonInclude]
    public string SelectedBy { get; private set; } = string.Empty;
    [JsonInclude]
    public DateTimeOffset SelectedAt { get; private set; }

    public static ModuleProfileSelection Create(ModuleProfileSelected selected)
    {
        var selection = new ModuleProfileSelection();
        selection.Apply(selected);
        return selection;
    }

    public void Apply(ModuleProfileSelected selected)
    {
        Id = selected.SelectionId;
        Project = selected.Project;
        Module = selected.Module;
        Profile = selected.Profile;
        SelectedBy = selected.SelectedBy;
        SelectedAt = selected.SelectedAt;
    }
}
