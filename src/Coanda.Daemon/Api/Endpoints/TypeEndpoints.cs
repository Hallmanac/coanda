using Coanda.Core.Types;
using Coanda.Daemon.Parcels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Coanda.Daemon.Api.Endpoints;

public static class TypeEndpoints
{
    public static IEndpointRouteBuilder MapTypeEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/types");

        group.MapGet("", async (HttpContext context, ParcelService parcels, string project, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<ParcelType> types = await parcels.TypesAsync(RequestPrincipal.PrincipalOf(context), project, cancellationToken);
                List<TypeView> views = [.. types.Select(t => new TypeView(t.Name, t.Hash, t.States, t.Resolutions))];
                return Results.Ok(views);
            })
            .Produces<List<TypeView>>();

        return routes;
    }
}
