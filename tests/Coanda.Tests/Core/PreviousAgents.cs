using System.Text.Json;
using Coanda.Core.Agents;

namespace Coanda.Tests.Core;

/// <summary>Shipped agents as an earlier build carried them, for tests of what a store seeded by that build holds.</summary>
public static class PreviousAgents
{
    /// <summary>The output the discovery agent was given before it listed the tasks it proposes in its answer's <c>parcels</c>.</summary>
    public const string DiscoveryOutputBeforeParcels =
        "End your reply with exactly one JSON document and nothing after it. The document is an object with a \"verdict\" field and an optional \"summary\" field laying out what you found and why. Use the verdict \"derive\" when the idea is understood well enough to be split into tasks, and describe those tasks in the summary. Use the verdict \"carry-forward\" when it should stay open for further discovery, and say in the summary what is still unknown.";

    /// <summary>
    /// The discovery agent as main shipped it before its answer described a <c>parcels</c> list: today's, with
    /// the output it had then and, as its schema content, the document the release shipped under its schema name.
    /// </summary>
    public static AgentDefinition DiscoveryBeforeParcels
    {
        get
        {
            AgentDefinition discovery = SoftwareAgents.Discovery;
            AnswerSchema named = NameOnlyAnswerSchemas.Find(NameOnlyAnswerSchemas.Discovery)
                ?? throw new InvalidOperationException("No name-only discovery schema.");
            using JsonDocument document = JsonDocument.Parse(named.Document);
            return discovery with
            {
                Persona = discovery.Persona with { Output = DiscoveryOutputBeforeParcels },
                AnswerSchemaContent = document.RootElement.Clone(),
            };
        }
    }
}
