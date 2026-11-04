using Coanda.Core.Agents;
using Coanda.Core.Definitions;

namespace Coanda.Daemon.Api.Endpoints;

public sealed record AgentView(string Name, int Version, ModelSpec Model, string Hash)
{
    public static AgentView From(Definition definition) => new(
        definition.Name, definition.VersionNumber, AgentDefinition.FromJson(definition.Content).Model, definition.ContentHash);
}
