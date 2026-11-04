using Coanda.Core.Parcels;
using Coanda.Core.Types;

namespace Coanda.Core.Software;

/// <summary>
/// The software engineering lifecycle, installed by default: an idea goes through discovery and
/// either carries forward or derives tasks and closes as split; a task goes refine, build, review,
/// closed. Both are ordinary <see cref="ParcelType"/> data with no privileges a user's type lacks.
/// Entering discovery starts the discovery flow and entering build starts the build flow; no other
/// state carries a flow. A task declares an objective and criteria, required to leave refine,
/// non-goals, required to leave no state, and a pull request, required to leave build; it binds the
/// required-fields gate to the moves out of refine and out of build. The idea declares no field and
/// binds no gate.
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
        Fields =
        [
            new FieldDeclaration { Name = "objective", Kind = FieldKinds.Text, RequiredToLeave = ["refine"] },
            new FieldDeclaration { Name = "criteria", Kind = FieldKinds.TextList, RequiredToLeave = ["refine"] },
            new FieldDeclaration { Name = "non-goals", Kind = FieldKinds.TextList },
            new FieldDeclaration { Name = "pull-request", Kind = FieldKinds.Reference, RequiredToLeave = ["build"] },
        ],
        FlowBindings = [new FlowBinding("build", "build")],
        GateBindings =
        [
            new GateBinding("refine", "build", RequiredFieldsGate.GateName),
            new GateBinding("build", "review", RequiredFieldsGate.GateName),
        ],
    };

    public static readonly IReadOnlyList<ParcelType> All = [Idea, Task];

    public static ParcelType? Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}
