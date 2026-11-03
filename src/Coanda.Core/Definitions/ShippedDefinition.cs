using Coanda.Core.Agents;
using Coanda.Core.Flows;
using Coanda.Core.Software;
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

    public static ShippedDefinition FromFlow(FlowDefinition flow) =>
        new(DefinitionKind.Flow, flow.Name, FlowDefinition.CanonicalJson(flow), flow.Hash);

    public static ShippedDefinition FromAgent(AgentDefinition agent) =>
        new(DefinitionKind.Agent, agent.Name, AgentDefinition.CanonicalJson(agent), agent.Hash);

    /// <summary>The parcel types the binary carries, as definitions.</summary>
    public static IReadOnlyList<ShippedDefinition> ShippedParcelTypes { get; } = [.. ShippedTypes.All.Select(FromParcelType)];

    /// <summary>The flows the binary carries, as definitions.</summary>
    public static IReadOnlyList<ShippedDefinition> ShippedFlowDefinitions { get; } = [.. ShippedFlows.All.Select(FromFlow)];

    /// <summary>The agents the binary carries, as definitions.</summary>
    public static IReadOnlyList<ShippedDefinition> ShippedAgentDefinitions { get; } = [.. ShippedAgents.All.Select(FromAgent)];

    /// <summary>Every definition the binary carries, in the order the daemon seeds it.</summary>
    public static IReadOnlyList<ShippedDefinition> All { get; } = [.. ShippedParcelTypes, .. ShippedFlowDefinitions, .. ShippedAgentDefinitions];
}
