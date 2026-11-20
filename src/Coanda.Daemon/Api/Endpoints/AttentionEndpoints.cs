using Coanda.Daemon.Attention;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Coanda.Daemon.Api.Endpoints;

public static class AttentionEndpoints
{
    public static IEndpointRouteBuilder MapAttentionEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/attention", async (HttpContext context, AttentionService attention, string? project, CancellationToken cancellationToken) =>
            {
                // A project the query names with an empty value reaches the service's refusal as empty,
                // rather than as though no project were named.
                string? named = context.Request.Query.ContainsKey(nameof(project))
                    ? project ?? string.Empty
                    : null;
                IReadOnlyList<PausedFlowRun> paused = await attention.ListAsync(RequestPrincipal.PrincipalOf(context), named, cancellationToken);
                List<AttentionView> views = [.. paused.Select(AttentionView.From)];
                return Results.Ok(views);
            })
            .Produces<List<AttentionView>>();

        return routes;
    }
}
