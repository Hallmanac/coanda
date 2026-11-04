using Coanda.Core.Definitions;
using Coanda.Core.Registry;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Store;
using Fisher;

namespace Coanda.Daemon.Contours;

/// <summary>The daemon's side of the contour queries: pass the registry, then ask resolution.</summary>
public sealed class ContourService(IDocumentStore store)
{
    private readonly DefinitionResolver definitions = new(store);

    /// <summary>The contours that resolution returns for the project, ordered by name.</summary>
    public async Task<IReadOnlyList<Definition>> ListAsync(Principal principal, string project, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("contour.list", principal);
        return await definitions.ResolveAllAsync(Tenants.RequireProject(project), DefinitionKind.Contour, cancellationToken);
    }
}
