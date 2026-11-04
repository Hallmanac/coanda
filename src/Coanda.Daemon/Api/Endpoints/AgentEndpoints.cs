using Coanda.Core.Definitions;
using Coanda.Daemon.Definitions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Coanda.Daemon.Api.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/agents");

        group.MapGet("", async (HttpContext context, DefinitionService definitions, string project, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<Definition> agents = await definitions.ListAgentsAsync(RequestPrincipal.PrincipalOf(context), project, cancellationToken);
                List<AgentView> views = [.. agents.Select(AgentView.From)];
                return Results.Ok(views);
            })
            .Produces<List<AgentView>>();

        return routes;
    }
}
