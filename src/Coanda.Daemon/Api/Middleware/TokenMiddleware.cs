using Coanda.Core.Registry;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Coanda.Daemon.Api.Middleware;

/// <summary>
/// Refuses every call that carries neither the daemon's token nor a session or module token the
/// daemon issued and still resolves, except <c>/health</c>, which the CLI probes before it has read
/// the token. A call presenting a session token acts as that session's principal, and one presenting
/// a module token acts as that module's principal, whatever principal header it carries.
/// </summary>
public static class TokenMiddleware
{
    private const string TokenPrincipalKey = "coanda.token-principal";

    public static IApplicationBuilder UseDaemonToken(this IApplicationBuilder app, string token, SessionTokens sessions, ModuleTokens modules) =>
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

            Principal? resolved = await sessions.ResolveAsync(presented, context.RequestAborted)
                ?? await modules.ResolveAsync(presented, context.RequestAborted);
            if (resolved is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new ErrorView("The call carried no valid daemon token."));
                return;
            }

            context.Items[TokenPrincipalKey] = resolved;
            await next(context);
        });

    /// <summary>
    /// The session or module principal the call's token resolved to, or null when it presented the
    /// daemon token.
    /// </summary>
    public static Principal? TokenPrincipalOf(HttpContext context) =>
        context.Items.TryGetValue(TokenPrincipalKey, out object? held) ? held as Principal : null;
}
