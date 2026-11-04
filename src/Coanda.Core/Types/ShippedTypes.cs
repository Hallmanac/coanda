namespace Coanda.Core.Types;

/// <summary>
/// The software engineering lifecycle, installed by default: an idea goes through discovery and
/// either carries forward or derives tasks and closes as split; a task goes refine, build, review,
/// closed. Both are ordinary <see cref="ParcelType"/> data with no privileges a user's type lacks.
/// Entering discovery starts the discovery flow and entering build starts the build flow; no other
/// state carries a flow, and neither type binds a gate.
/// </summary>
public static class ShippedTypes
{
    public const string Closed = "closed";
    public const string Paused = "paused";

    public static readonly ParcelType Idea = new()
    {
        Name = "idea",
        States = ["open", "discovery", Paused, Closed],
        EntryStates = ["open"],
        TerminalState = Closed,
        Resolutions = ["split", "concluded", "archived"],
        PausedState = Paused,
        Moves = new Dictionary<string, IReadOnlyList<string>>
        {
            ["open"] = ["discovery"],
        },
        FlowBindings = [new FlowBinding("discovery", "discovery")],
    };

    public static readonly ParcelType Task = new()
    {
        Name = "task",
        States = ["refine", "build", "review", Paused, Closed],
        EntryStates = ["refine"],
        TerminalState = Closed,
        Resolutions = ["completed", "abandoned", "superseded", "duplicate"],
        PausedState = Paused,
        Moves = new Dictionary<string, IReadOnlyList<string>>
        {
            ["refine"] = ["build"],
            ["build"] = ["review"],
            ["review"] = ["build"],
        },
        FlowBindings = [new FlowBinding("build", "build")],
    };

    public static readonly IReadOnlyList<ParcelType> All = [Idea, Task];

    public static ParcelType? Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}
