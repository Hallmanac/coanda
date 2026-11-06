using Coanda.Core.Identity;
using Coanda.Core.Registry;
using Coanda.Daemon.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Coanda.Daemon.Api.Endpoints;

/// <summary>This node's identity, its vouches for other nodes, and its join of a project another node owns.</summary>
public static class NodeEndpoints
{
    public static IEndpointRouteBuilder MapNodeEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/node", (HttpContext context, NodeIdentity identity) =>
            {
                CommandRegistry.Require("node.show", RequestPrincipal.PrincipalOf(context));
                return Results.Ok(new NodeView(identity.NodeId, identity.Key.PublicKey, KeyFingerprint.Of(identity.Key.PublicKey)));
            })
            .Produces<NodeView>();

        routes.MapPost("/node/vouch", async (HttpContext context, NodeVoucher voucher, NodeVouchRequest request, CancellationToken cancellationToken) =>
            Results.Ok(await voucher.VouchAsync(
                RequestPrincipal.PrincipalOf(context), request.Project ?? string.Empty, request.NodeId ?? string.Empty, request.Key ?? string.Empty, cancellationToken)))
            .Produces<NodeVouchResult>();

        routes.MapPost("/join", async (HttpContext context, NodeJoiner joiner, JoinRequest request, CancellationToken cancellationToken) =>
            Results.Ok(await joiner.JoinAsync(RequestPrincipal.PrincipalOf(context), request.Remote ?? string.Empty, request.Root ?? string.Empty, cancellationToken)))
            .Produces<JoinResult>();

        return routes;
    }
}
