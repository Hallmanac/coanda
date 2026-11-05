using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Daemon.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Daemon.Api.Endpoints;

/// <summary>The principal a call acts as, read the same way by every endpoint.</summary>
public static class RequestPrincipal
{
    /// <summary>
    /// A call that presented a session or module token acts as the principal that token resolved to,
    /// and its principal header is not read. A call holding only the daemon token acts as the node's
    /// owner (<see cref="NodeOwner"/>) when its principal header is missing, blank, or names exactly the
    /// owner's id, and is refused, before any service runs, when the header names anyone else.
    /// </summary>
    public static Principal PrincipalOf(HttpContext context) =>
        TokenMiddleware.TokenPrincipalOf(context) switch
        {
            { } resolved => resolved,
            null => OwnerOf(context),
        };

    private static Principal OwnerOf(HttpContext context)
    {
        NodeOwner owner = context.RequestServices.GetRequiredService<NodeOwner>();
        string named = context.Request.Headers.TryGetValue(DaemonHost.PrincipalHeader, out var header)
            ? header.ToString()
            : string.Empty;

        return string.IsNullOrWhiteSpace(named) || string.Equals(named, owner.Id, StringComparison.Ordinal)
            ? owner.Principal
            : throw new DomainException(
                $"The {DaemonHost.PrincipalHeader} header names '{named}', but a call holding only the daemon token acts as the node's owner, '{owner.Id}'; send '{owner.Id}' or no header.");
    }
}
