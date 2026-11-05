using Coanda.Core.Registry;
using Coanda.Daemon.Api.Middleware;
using Microsoft.AspNetCore.Http;

namespace Coanda.Daemon.Api.Endpoints;

/// <summary>The principal a call acts as, read the same way by every endpoint.</summary>
public static class RequestPrincipal
{
    /// <summary>
    /// A call that presented a session or module token acts as the principal that token resolved to,
    /// and its principal header is not read. Otherwise phase 0 knows one principal: whoever holds the daemon token is
    /// the local owner.
    /// </summary>
    public static Principal PrincipalOf(HttpContext context) =>
        TokenMiddleware.TokenPrincipalOf(context) switch
        {
            { } resolved => resolved,
            null when context.Request.Headers.TryGetValue(DaemonHost.PrincipalHeader, out var id) && !string.IsNullOrWhiteSpace(id) =>
                new Principal(id.ToString(), Role.Owner),
            null => Principal.LocalOwner,
        };
}
