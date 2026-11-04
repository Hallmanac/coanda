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

        group.MapPost("/{id:guid}/pause", async (HttpContext context, FlowService flows, Guid id, FlowPauseRequest request, CancellationToken cancellationToken) =>
            {
                // A resume performs the current step again, so the lever names that step, read in the
                // pause's own write so it is the step the flow is paused at.
                string project = request.Project.Trim();
                Flow flow = await flows.PauseToPerformAgainAsync(RequestPrincipal.PrincipalOf(context), project, id, request.Reason, cancellationToken);
                return Results.Ok(FlowView.From(project, flow));
            })
            .Produces<FlowView>();

        group.MapPost("/{id:guid}/stop", async (HttpContext context, FlowService flows, Guid id, FlowStopRequest request, CancellationToken cancellationToken) =>
            {
                string project = request.Project.Trim();
                Flow flow = await flows.StopAsync(RequestPrincipal.PrincipalOf(context), project, id, request.Reason, cancellationToken);
                return Results.Ok(FlowView.From(project, flow));
            })
            .Produces<FlowView>();

        group.MapPost("/{id:guid}/resume", async (HttpContext context, FlowService flows, Guid id, FlowResumeRequest request, CancellationToken cancellationToken) =>
            {
                // The service hands a resumed flow to the daemon's workers; the resume answers without waiting for it.
                string project = request.Project.Trim();
                Flow flow = await flows.ResumeAsync(RequestPrincipal.PrincipalOf(context), project, id, request.Note, cancellationToken);
                return Results.Ok(FlowView.From(project, flow));
            })
            .Produces<FlowView>();

        return routes;
    }
}
