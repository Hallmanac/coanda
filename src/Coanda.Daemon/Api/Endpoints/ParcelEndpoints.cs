using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Daemon.Parcels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Coanda.Daemon.Api.Endpoints;

public static class ParcelEndpoints
{
    public static IEndpointRouteBuilder MapParcelEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/parcels");

        group.MapGet("", async (HttpContext context, ParcelService parcels, string project, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<Parcel> list = await parcels.ListAsync(RequestPrincipal.PrincipalOf(context), project, cancellationToken);
                List<ParcelView> views = [.. list.Select(ParcelView.From)];
                return Results.Ok(views);
            })
            .Produces<List<ParcelView>>();

        group.MapGet("/{id:guid}", async (HttpContext context, ParcelService parcels, Guid id, string project, CancellationToken cancellationToken) =>
            {
                Parcel? parcel = await parcels.ShowAsync(RequestPrincipal.PrincipalOf(context), project, id, cancellationToken);
                return parcel is null
                    ? Results.NotFound(new ErrorView($"No parcel {Ids.Short(id)} in project '{project}'."))
                    : Results.Ok(ParcelView.From(parcel));
            })
            .Produces<ParcelView>()
            .Produces<ErrorView>(StatusCodes.Status404NotFound);

        group.MapPost("", async (HttpContext context, ParcelService parcels, ParcelAddRequest request, CancellationToken cancellationToken) =>
            {
                Parcel parcel = await parcels.AddAsync(RequestPrincipal.PrincipalOf(context), request.Project, request.Type, request.Title, request.State, cancellationToken);
                return Results.Created($"/parcels/{parcel.Id}", ParcelView.From(parcel));
            })
            .Produces<ParcelView>(StatusCodes.Status201Created);

        group.MapPost("/move", async (HttpContext context, ParcelService parcels, ParcelMoveRequest request, CancellationToken cancellationToken) =>
            {
                Parcel parcel = await parcels.MoveAsync(RequestPrincipal.PrincipalOf(context), request.Project, request.ParcelId, request.To, cancellationToken);
                return Results.Ok(ParcelView.From(parcel));
            })
            .Produces<ParcelView>();

        group.MapPost("/close", async (HttpContext context, ParcelService parcels, ParcelCloseRequest request, CancellationToken cancellationToken) =>
            {
                Parcel parcel = await parcels.CloseAsync(RequestPrincipal.PrincipalOf(context), request.Project, request.ParcelId, request.Resolution, request.Reason, cancellationToken);
                return Results.Ok(ParcelView.From(parcel));
            })
            .Produces<ParcelView>();

        return routes;
    }
}
