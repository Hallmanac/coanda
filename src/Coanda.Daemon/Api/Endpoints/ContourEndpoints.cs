using Coanda.Core.Definitions;
using Coanda.Daemon.Contours;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Coanda.Daemon.Api.Endpoints;

public static class ContourEndpoints
{
    public static IEndpointRouteBuilder MapContourEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/contours");

        group.MapGet("", async (HttpContext context, ContourService contours, string project, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<Definition> resolved = await contours.ListAsync(RequestPrincipal.PrincipalOf(context), project, cancellationToken);
                List<ContourView> views = [.. resolved.Select(d => new ContourView(d.Name, d.VersionNumber, d.ContentHash))];
                return Results.Ok(views);
            })
            .Produces<List<ContourView>>();

        return routes;
    }
}
