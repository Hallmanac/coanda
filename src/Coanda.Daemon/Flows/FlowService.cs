using Coanda.Core.Definitions;
using Coanda.Core.Registry;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Store;
using Fisher;

namespace Coanda.Daemon.Flows;

/// <summary>The daemon's side of the flow queries: pass the registry, then ask resolution.</summary>
public sealed class FlowService(IDocumentStore store)
{
    private readonly DefinitionResolver definitions = new(store);

    /// <summary>The flows that resolution returns for the project, ordered by name.</summary>
    public async Task<IReadOnlyList<Definition>> ListAsync(Principal principal, string project, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow.list", principal);
        return await definitions.ResolveAllAsync(Tenants.RequireProject(project), DefinitionKind.Flow, cancellationToken);
    }
}
