using System.Text.Json;

namespace Coanda.Sdk.Contract;

/// <summary>
/// One system step a module brings: its module-prefixed name, the schema of the arguments a flow
/// author writes for it, and the schema of the result its done answer carries.
/// </summary>
public sealed record StepDescription(string Name, JsonElement ArgumentSchema, JsonElement ResultSchema);
