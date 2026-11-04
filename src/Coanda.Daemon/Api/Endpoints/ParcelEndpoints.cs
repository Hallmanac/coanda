using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Types;
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
                Principal principal = RequestPrincipal.PrincipalOf(context);
                IReadOnlyList<Parcel> list = await parcels.ListAsync(principal, project, cancellationToken);
                var types = new Dictionary<string, ParcelType?>(StringComparer.Ordinal);
                List<ParcelView> views = [];
                foreach (Parcel parcel in list)
                {
                    if (!types.TryGetValue(parcel.TypeHash, out ParcelType? type))
                    {
                        type = await parcels.TypeVersionAsync(principal, project, parcel.TypeHash, cancellationToken);
                        types[parcel.TypeHash] = type;
                    }

                    views.Add(ParcelView.From(parcel, type));
                }

                return Results.Ok(views);
            })
            .Produces<List<ParcelView>>();

        group.MapGet("/{id:guid}", async (HttpContext context, ParcelService parcels, Guid id, string project, CancellationToken cancellationToken) =>
            {
                Principal principal = RequestPrincipal.PrincipalOf(context);
                Parcel? parcel = await parcels.ShowAsync(principal, project, id, cancellationToken);
                return parcel is null
                    ? Results.NotFound(new ErrorView($"No parcel {Ids.Short(id)} in project '{project}'."))
                    : Results.Ok(await ViewAsync(parcels, principal, project, parcel, cancellationToken));
            })
            .Produces<ParcelView>()
            .Produces<ErrorView>(StatusCodes.Status404NotFound);

        group.MapPost("", async (HttpContext context, ParcelService parcels, ParcelAddRequest request, CancellationToken cancellationToken) =>
            {
                Principal principal = RequestPrincipal.PrincipalOf(context);
                Parcel parcel = await parcels.AddAsync(principal, request.Project, request.Type, request.Title, request.Stage, cancellationToken);
                return Results.Created($"/parcels/{parcel.Id}", await ViewAsync(parcels, principal, request.Project, parcel, cancellationToken));
            })
            .Produces<ParcelView>(StatusCodes.Status201Created);

        group.MapPost("/move", async (HttpContext context, ParcelService parcels, ParcelMoveRequest request, CancellationToken cancellationToken) =>
            {
                Principal principal = RequestPrincipal.PrincipalOf(context);
                Parcel parcel = await parcels.MoveAsync(principal, request.Project, request.ParcelId, request.To, cancellationToken);
                return Results.Ok(await ViewAsync(parcels, principal, request.Project, parcel, cancellationToken));
            })
            .Produces<ParcelView>();

        group.MapPost("/close", async (HttpContext context, ParcelService parcels, ParcelCloseRequest request, CancellationToken cancellationToken) =>
            {
                Principal principal = RequestPrincipal.PrincipalOf(context);
                Parcel parcel = await parcels.CloseAsync(principal, request.Project, request.ParcelId, request.Resolution, request.Reason, cancellationToken);
                return Results.Ok(await ViewAsync(parcels, principal, request.Project, parcel, cancellationToken));
            })
            .Produces<ParcelView>();

        group.MapPost("/set-field", async (HttpContext context, ParcelService parcels, ParcelSetFieldRequest request, CancellationToken cancellationToken) =>
            {
                Principal principal = RequestPrincipal.PrincipalOf(context);
                Parcel parcel = await parcels.SetFieldAsync(principal, request.Project, request.ParcelId, request.Field, request.Value, cancellationToken);
                return Results.Ok(await ViewAsync(parcels, principal, request.Project, parcel, cancellationToken));
            })
            .Produces<ParcelView>();

        return routes;
    }

    /// <summary>The parcel with the fields of the type version it was created under.</summary>
    private static async Task<ParcelView> ViewAsync(
        ParcelService parcels, Principal principal, string project, Parcel parcel, CancellationToken cancellationToken) =>
        ParcelView.From(parcel, await parcels.TypeVersionAsync(principal, project, parcel.TypeHash, cancellationToken));
}
