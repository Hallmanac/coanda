using Coanda.Core.Registry;
using Coanda.Daemon.FlowRuns;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Coanda.Daemon.Api.Middleware;

/// <summary>
/// Refuses every call that carries neither the daemon's token nor a session token the daemon
/// issued and still resolves, except <c>/health</c>, which the CLI probes before it has read the
/// token. A call presenting a session token acts as that session's principal, whatever principal
/// header it carries.
/// </summary>
public static class TokenMiddleware
{
    private const string SessionPrincipalKey = "coanda.session-principal";

    public static IApplicationBuilder UseDaemonToken(this IApplicationBuilder app, string token, SessionTokens sessions) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/health")
            {
                await next(context);
                return;
            }

            string presented = context.Request.Headers.TryGetValue(DaemonHost.TokenHeader, out var header) ? header.ToString() : string.Empty;
            if (presented == token)
            {
                await next(context);
                return;
            }

            Principal? session = await sessions.ResolveAsync(presented, context.RequestAborted);
            if (session is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new ErrorView("The call carried no valid daemon token."));
                return;
            }

            context.Items[SessionPrincipalKey] = session;
            await next(context);
        });

    /// <summary>The session principal the call's token resolved to, or null when it presented the daemon token.</summary>
    public static Principal? SessionPrincipalOf(HttpContext context) =>
        context.Items.TryGetValue(SessionPrincipalKey, out object? held) ? held as Principal : null;
}
