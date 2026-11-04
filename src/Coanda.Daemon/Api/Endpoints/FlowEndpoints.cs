using Coanda.Core.Definitions;
using Coanda.Daemon.Flows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Coanda.Daemon.Api.Endpoints;

public static class FlowEndpoints
{
    public static IEndpointRouteBuilder MapFlowEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/flows");

        group.MapGet("", async (HttpContext context, FlowService flows, string project, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<Definition> resolved = await flows.ListAsync(RequestPrincipal.PrincipalOf(context), project, cancellationToken);
                List<FlowView> views = [.. resolved.Select(d => new FlowView(d.Name, d.VersionNumber, d.ContentHash))];
                return Results.Ok(views);
            })
            .Produces<List<FlowView>>();

        return routes;
    }
}
