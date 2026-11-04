using Coanda.Core.Registry;
using Microsoft.AspNetCore.Http;

namespace Coanda.Daemon.Api.Endpoints;

/// <summary>The principal a call acts as, read the same way by every endpoint.</summary>
public static class RequestPrincipal
{
    /// <summary>Phase 0 knows one principal: whoever holds the token is the local owner.</summary>
    public static Principal PrincipalOf(HttpContext context) =>
        context.Request.Headers.TryGetValue(DaemonHost.PrincipalHeader, out var id) && !string.IsNullOrWhiteSpace(id)
            ? new Principal(id.ToString(), Role.Owner)
            : Principal.LocalOwner;
}
