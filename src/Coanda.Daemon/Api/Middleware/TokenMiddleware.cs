using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Coanda.Daemon.Api.Middleware;

/// <summary>
/// Refuses every call that does not carry the daemon's token, except <c>/health</c>, which the CLI
/// probes before it has read the token.
/// </summary>
public static class TokenMiddleware
{
    public static IApplicationBuilder UseDaemonToken(this IApplicationBuilder app, string token) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/health")
            {
                await next(context);
                return;
            }

            if (!context.Request.Headers.TryGetValue(DaemonHost.TokenHeader, out var presented) || presented != token)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new ErrorView("The call carried no valid daemon token."));
                return;
            }

            await next(context);
        });
}
