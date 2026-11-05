using Coanda.Core.Types;

namespace Coanda.Tests;

/// <summary>The software module's parcel types, idea and task, read from its definitions folder.</summary>
public static class SoftwareTypes
{
    public static readonly ParcelType Idea = ParcelType.FromJson(SoftwareDefinitions.Read("types", "idea"));

    public static readonly ParcelType Task = ParcelType.FromJson(SoftwareDefinitions.Read("types", "task"));

    public static readonly IReadOnlyList<ParcelType> All = [Idea, Task];

    /// <summary>The task type's terminal stage, the stage a closed parcel of either type is in.</summary>
    public static string Closed => Task.TerminalStage;

    /// <summary>The task type's paused stage.</summary>
    public static string Paused => Task.PausedStage;

    public static ParcelType? Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}
