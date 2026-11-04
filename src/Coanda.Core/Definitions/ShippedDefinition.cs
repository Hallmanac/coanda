using Coanda.Core.Agents;
using Coanda.Core.Flows;
using Coanda.Core.Types;

namespace Coanda.Core.Definitions;

/// <summary>
/// One definition the binary carries, ready to seed into the store. Seeding is generic over
/// <see cref="DefinitionKind"/>: the flow and agent definitions arrive through the same shape.
/// </summary>
public sealed record ShippedDefinition(DefinitionKind Kind, string Name, string Content, string ContentHash)
{
    public static ShippedDefinition FromParcelType(ParcelType type) =>
        new(DefinitionKind.ParcelType, type.Name, ParcelType.CanonicalJson(type), type.Hash);

    public static ShippedDefinition FromFlow(Flow flow) =>
        new(DefinitionKind.Flow, flow.Name, Flow.CanonicalJson(flow), flow.Hash);

    public static ShippedDefinition FromAgent(AgentDefinition agent) =>
        new(DefinitionKind.Agent, agent.Name, AgentDefinition.CanonicalJson(agent), agent.Hash);
}
