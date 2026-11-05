using Coanda.Core.Types;

namespace Coanda.Tests;

/// <summary>The software module's parcel types, idea and task, read from its definitions folder.</summary>
public static class SoftwareTypes
{
    public static readonly ParcelType Idea = ParcelType.FromJson(SoftwareDefinitions.Read("types", "idea"));

    public static readonly ParcelType Task = ParcelType.FromJson(SoftwareDefinitions.Read("types", "task"));

    public static readonly IReadOnlyList<ParcelType> All = [Idea, Task];

    /// <summary>
    /// The task type without the fields the software module owns, as a project or the owner writes a task
    /// type of its own with no fork base, which may name no field's owner.
    /// </summary>
    public static readonly ParcelType UnownedTask = Task with { Fields = [.. Task.Fields.Where(f => f.Owner is null)] };

    /// <summary>The task type's terminal stage, the stage a closed parcel of either type is in.</summary>
    public static string Closed => Task.TerminalStage;

    /// <summary>The task type's paused stage.</summary>
    public static string Paused => Task.PausedStage;

    public static ParcelType? Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}
