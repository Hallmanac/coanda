using Coanda.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Coanda.Daemon.Api.Middleware;

/// <summary>Answers a <see cref="DomainException"/> an endpoint throws with 422 and the rule it quotes.</summary>
public static class DomainExceptionMiddleware
{
    public static IApplicationBuilder UseDomainExceptionMapping(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (DomainException exception)
            {
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                await context.Response.WriteAsJsonAsync(new ErrorView(exception.Message));
            }
        });
}
