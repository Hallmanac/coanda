using System.Text.Json;

namespace Coanda.Daemon.Api.Endpoints;

/// <summary>Gives the module's replicated setting <see cref="Name"/> in the project the value <see cref="Value"/>, any JSON its settings schema accepts.</summary>
public sealed record ProjectModuleSetRequest(string Project, string Module, string Name, JsonElement Value);

/// <summary>Selects for the module in the project, on this node, the credential profile <see cref="Profile"/>, an entry directly inside the module's area.</summary>
public sealed record ProjectModuleSelectProfileRequest(string Project, string Module, string Profile);
