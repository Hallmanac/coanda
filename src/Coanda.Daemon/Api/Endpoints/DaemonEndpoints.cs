using Coanda.Core.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace Coanda.Daemon.Api.Endpoints;

/// <summary>The daemon's own routes. They share no prefix, so they are mapped without a group.</summary>
public static class DaemonEndpoints
{
    public static IEndpointRouteBuilder MapDaemonEndpoints(this IEndpointRouteBuilder routes)
    {
        // Outside the token check: the CLI probes it before it has read the token.
        routes.MapGet("/health", () => Results.Ok(new { ok = true }));

        routes.MapGet("/status", (HttpContext context, NodeHome home) =>
            {
                CommandRegistry.Require("daemon.status", RequestPrincipal.PrincipalOf(context));
                DaemonInfo? info = DaemonInfo.Load(home);
                return info is null
                    ? Results.NotFound(new ErrorView("The daemon has not written its address yet."))
                    : Results.Ok(new DaemonStatusView(info.Pid, info.Port, info.StartedAt, info.Version, home.Path, home.DatabasePath));
            })
            .Produces<DaemonStatusView>()
            .Produces<ErrorView>(StatusCodes.Status404NotFound);

        routes.MapPost("/stop", (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            CommandRegistry.Require("daemon.stop", RequestPrincipal.PrincipalOf(context));
            lifetime.StopApplication();
            return Results.Ok(new { stopping = true });
        });

        return routes;
    }
}
