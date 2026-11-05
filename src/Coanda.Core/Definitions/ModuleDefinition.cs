using Coanda.Core.Agents;
using Coanda.Core.Flows;
using Coanda.Core.Types;

namespace Coanda.Core.Definitions;

/// <summary>
/// One definition a module brings, ready to import into module scope: its kind, its name, its
/// canonical content, and that content's hash. Import is generic over <see cref="DefinitionKind"/>:
/// a parcel type, a flow, and an agent arrive through the same shape.
/// </summary>
public sealed record ModuleDefinition(DefinitionKind Kind, string Name, string Content, string ContentHash)
{
    public static ModuleDefinition FromParcelType(ParcelType type) =>
        new(DefinitionKind.ParcelType, type.Name, ParcelType.CanonicalJson(type), type.Hash);

    public static ModuleDefinition FromFlow(Flow flow) =>
        new(DefinitionKind.Flow, flow.Name, Flow.CanonicalJson(flow), flow.Hash);

    public static ModuleDefinition FromAgent(AgentDefinition agent) =>
        new(DefinitionKind.Agent, agent.Name, AgentDefinition.CanonicalJson(agent), agent.Hash);

    /// <summary>
    /// Reads <paramref name="json"/> as a definition of <paramref name="kind"/> through that kind's
    /// canonical read, which refuses anything else, and carries it as the kind's canonical JSON with
    /// its hash, so how the text is laid out changes neither what is stored nor its hash.
    /// </summary>
    public static ModuleDefinition Read(DefinitionKind kind, string json) => kind switch
    {
        DefinitionKind.ParcelType => FromParcelType(ParcelType.FromJson(json)),
        DefinitionKind.Flow => FromFlow(Flow.FromJson(json)),
        DefinitionKind.Agent => FromAgent(AgentDefinition.FromJson(json)),
        _ => throw new DomainException($"'{kind}' is not a kind of definition."),
    };
}
