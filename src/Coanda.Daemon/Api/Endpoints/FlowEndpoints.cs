using Coanda.Core;
using Coanda.Core.Flows;
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

        group.MapGet("", async (HttpContext context, FlowService flows, string project, Guid? parcel, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<Flow> list = await flows.ListAsync(RequestPrincipal.PrincipalOf(context), project, parcel, cancellationToken);
                List<FlowView> views = [.. list.Select(f => FlowView.From(project.Trim(), f))];
                return Results.Ok(views);
            })
            .Produces<List<FlowView>>();

        group.MapGet("/{id:guid}", async (HttpContext context, FlowService flows, Guid id, string project, CancellationToken cancellationToken) =>
            {
                Flow? flow = await flows.ShowAsync(RequestPrincipal.PrincipalOf(context), project, id, cancellationToken);
                return flow is null
                    ? Results.NotFound(new ErrorView($"No flow {Ids.Short(id)} in project '{project}'."))
                    : Results.Ok(FlowView.From(project.Trim(), flow));
            })
            .Produces<FlowView>()
            .Produces<ErrorView>(StatusCodes.Status404NotFound);

        group.MapPost("", async (HttpContext context, FlowService flows, IFlowAdvancer advancer, FlowStartRequest request, CancellationToken cancellationToken) =>
            {
                string project = request.Project.Trim();
                Flow flow = await flows.StartAsync(RequestPrincipal.PrincipalOf(context), project, request.ParcelId, request.Contour, cancellationToken);

                // The flow advances on the daemon's own workers; the start answers without waiting for it.
                _ = advancer.RequestAdvance(project, flow.Id);
                return Results.Created($"/flows/{flow.Id}", FlowView.From(project, flow));
            })
            .Produces<FlowView>(StatusCodes.Status201Created);

        return routes;
    }
}
