using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.Registry;
using Coanda.Daemon.FlowRuns;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Coanda.Daemon.Api.Endpoints;

public static class FlowRunEndpoints
{
    public static IEndpointRouteBuilder MapFlowRunEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/flow-runs");

        group.MapGet("", async (HttpContext context, FlowRunService flowRuns, string project, Guid? parcel, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<FlowRun> list = await flowRuns.ListAsync(RequestPrincipal.PrincipalOf(context), project, parcel, cancellationToken);
                List<FlowRunView> views = [.. list.Select(f => FlowRunView.From(project.Trim(), f))];
                return Results.Ok(views);
            })
            .Produces<List<FlowRunView>>();

        group.MapGet("/{id:guid}", async (HttpContext context, FlowRunService flowRuns, Guid id, string project, CancellationToken cancellationToken) =>
            {
                FlowRun? flowRun = await flowRuns.ShowAsync(RequestPrincipal.PrincipalOf(context), project, id, cancellationToken);
                return flowRun is null
                    ? Results.NotFound(new ErrorView($"No flow run {Ids.Short(id)} in project '{project}'."))
                    : Results.Ok(FlowRunView.From(project.Trim(), flowRun));
            })
            .Produces<FlowRunView>()
            .Produces<ErrorView>(StatusCodes.Status404NotFound);

        group.MapPost("", async (HttpContext context, FlowRunService flowRuns, IFlowRunAdvancer advancer, FlowRunStartRequest request, CancellationToken cancellationToken) =>
            {
                string project = request.Project.Trim();
                FlowRun flowRun = await flowRuns.StartAsync(RequestPrincipal.PrincipalOf(context), project, request.ParcelId, request.Flow, cancellationToken);

                // The flow run advances on the daemon's own workers; the start answers without waiting for it.
                _ = advancer.RequestAdvance(project, flowRun.Id);
                return Results.Created($"/flow-runs/{flowRun.Id}", FlowRunView.From(project, flowRun));
            })
            .Produces<FlowRunView>(StatusCodes.Status201Created);

        group.MapPost("/{id:guid}/pause", async (HttpContext context, FlowRunService flowRuns, Guid id, FlowRunPauseRequest request, CancellationToken cancellationToken) =>
            {
                // The service computes no lever, and a resume performs the current step again, so the
                // lever names that step. An id the project does not hold reads as no flow run; the pause
                // itself then refuses it with the service's own rule.
                string project = request.Project.Trim();
                Principal principal = RequestPrincipal.PrincipalOf(context);
                FlowRun? current = await flowRuns.ShowAsync(principal, project, id, cancellationToken);
                string lever = FlowRunLevers.PerformAgain(current?.CurrentStep ?? string.Empty);
                FlowRun flowRun = await flowRuns.PauseAsync(principal, project, id, request.Reason, lever, cancellationToken);
                return Results.Ok(FlowRunView.From(project, flowRun));
            })
            .Produces<FlowRunView>();

        group.MapPost("/{id:guid}/stop", async (HttpContext context, FlowRunService flowRuns, Guid id, FlowRunStopRequest request, CancellationToken cancellationToken) =>
            {
                string project = request.Project.Trim();
                FlowRun flowRun = await flowRuns.StopAsync(RequestPrincipal.PrincipalOf(context), project, id, request.Reason, cancellationToken);
                return Results.Ok(FlowRunView.From(project, flowRun));
            })
            .Produces<FlowRunView>();

        group.MapPost("/{id:guid}/resume", async (HttpContext context, FlowRunService flowRuns, Guid id, FlowRunResumeRequest request, CancellationToken cancellationToken) =>
            {
                // The service hands a resumed flow run to the daemon's workers; the resume answers without waiting for it.
                string project = request.Project.Trim();
                FlowRun flowRun = await flowRuns.ResumeAsync(RequestPrincipal.PrincipalOf(context), project, id, request.Note, cancellationToken);
                return Results.Ok(FlowRunView.From(project, flowRun));
            })
            .Produces<FlowRunView>();

        return routes;
    }
}
