using System.Text.Json;

namespace Coanda.Core.Flows;

/// <summary>
/// What the argument schema of the system step <paramref name="systemStep"/> says of a flow step's
/// <paramref name="arguments"/>: each way it refuses them, or none when it accepts them, when the step
/// is not one a schema governs, or when the step's provider declares no argument schema.
/// </summary>
public delegate IReadOnlyList<StepArgumentComplaint> StepArgumentCheck(string systemStep, IReadOnlyDictionary<string, JsonElement> arguments);
