using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon.Triggers;
using Coanda.Sdk.Contract;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Coanda.Daemon.Api.Endpoints;

/// <summary>
/// External events. Recording one takes the module contract's recorded event as its body and answers
/// with the event the project's record holds for its kind and external id, the one this call recorded or
/// the one an earlier call did, without saying which.
/// </summary>
public static class EventEndpoints
{
    public const string Path = "/events";

    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup(Path);

        group.MapPost("", async (HttpContext context, TriggerService triggers, RecordedEvent request, CancellationToken cancellationToken) =>
            {
                Principal principal = RequestPrincipal.PrincipalOf(context);
                RequireWhole(request);
                ExternalEvent recorded = await triggers.RecordEventAsync(
                    principal, request.Project, request.Kind, request.ExternalId, request.ParcelId, request.Fields, cancellationToken);
                return Results.Ok(ExternalEventView.From(recorded));
            })
            .Produces<ExternalEventView>()
            .Produces<ErrorView>(StatusCodes.Status422UnprocessableEntity);

        return routes;
    }

    /// <summary>
    /// Refuses a body that leaves out a required member or gives it as null, or gives a field whose value
    /// is null. The binder fills a member the body leaves out with null, whatever its declared type, and
    /// the rules past this point read every member as present.
    /// </summary>
    private static void RequireWhole(RecordedEvent request)
    {
        string? refusal = request switch
        {
            { Project: null } => Missing("project"),
            { Kind: null } => Missing("kind"),
            { ExternalId: null } => Missing("externalId"),
            { Fields: null } => Missing("fields"),
            { Fields: var fields } when fields.Where(f => f.Value is null).Select(f => f.Key).FirstOrDefault() is { } field =>
                $"The event's field '{field}' is null; every field of an event is a string.",
            _ => null,
        };
        if (refusal is not null)
        {
            throw new DomainException(refusal);
        }
    }

    private static string Missing(string member) => $"An event to record needs its '{member}'; the request left it out or gave it as null.";
}
