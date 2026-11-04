using Coanda.Core.Parcels;
using Coanda.Core.Types;

namespace Coanda.Core.Software;

/// <summary>
/// The software engineering lifecycle, installed by default: an idea goes through discovery and
/// either carries forward or derives tasks and closes as split; a task goes refine, build, review,
/// closed. Both are ordinary <see cref="ParcelType"/> data with no privileges a user's type lacks.
/// Entering discovery starts the discovery flow and entering build starts the build flow; no other
/// stage carries a flow. A task declares an objective and criteria, required to leave refine,
/// non-goals, required to leave no stage, and a pull request, required to leave build; it binds the
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
        Stages = ["open", "discovery", Paused, Closed],
        EntryStages = ["open"],
        TerminalStage = Closed,
        Resolutions = ["split", "concluded", "archived"],
        PausedStage = Paused,
        Moves = new Dictionary<string, IReadOnlyList<string>>
        {
            ["open"] = ["discovery"],
        },
        FlowBindings = [new FlowBinding("discovery", "discovery")],
    };

    public static readonly ParcelType Task = new()
    {
        Name = "task",
        Stages = ["refine", "build", "review", Paused, Closed],
        EntryStages = ["refine"],
        TerminalStage = Closed,
        Resolutions = ["completed", "abandoned", "superseded", "duplicate"],
        PausedStage = Paused,
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
