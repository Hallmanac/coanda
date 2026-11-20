using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Daemon.Definitions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Coanda.Daemon.Api.Endpoints;

/// <summary>
/// The person's road to a project's definitions: list every one that can govern the project, show one
/// with its content, draft one from a file in a module's shape, and publish or retire one by its full id.
/// </summary>
public static class DefinitionEndpoints
{
    public static IEndpointRouteBuilder MapDefinitionEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/definitions");

        group.MapGet("", async (HttpContext context, DefinitionService definitions, string project, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<Definition> held = await definitions.ListAsync(RequestPrincipal.PrincipalOf(context), project, cancellationToken);
                List<DefinitionView> views = [.. held.Select(DefinitionView.From)];
                return Results.Ok(views);
            })
            .Produces<List<DefinitionView>>();

        group.MapGet("/{id:guid}", async (HttpContext context, DefinitionService definitions, Guid id, string project, CancellationToken cancellationToken) =>
            {
                Definition? definition = await definitions.ShowAsync(RequestPrincipal.PrincipalOf(context), project, id, cancellationToken);
                return definition is null
                    ? Results.NotFound(new ErrorView($"No definition {Ids.Short(id)} can govern project '{project}'."))
                    : Results.Ok(DefinitionDetailView.From(definition));
            })
            .Produces<DefinitionDetailView>()
            .Produces<ErrorView>(StatusCodes.Status404NotFound);

        group.MapPost("", async (HttpContext context, DefinitionService definitions, DefinitionDraftRequest request, CancellationToken cancellationToken) =>
            {
                Definition drafted = await definitions.DraftAsync(
                    RequestPrincipal.PrincipalOf(context),
                    DefinitionWords.KindOf(request.Kind),
                    DefinitionWords.ScopeOf(request.Scope),
                    request.Project,
                    request.Content,
                    request.Fork,
                    cancellationToken);
                return Results.Created($"/definitions/{drafted.Id}", DefinitionView.From(drafted));
            })
            .Produces<DefinitionView>(StatusCodes.Status201Created);

        group.MapPost("/publish", async (HttpContext context, DefinitionService definitions, DefinitionIdRequest request, CancellationToken cancellationToken) =>
            {
                Definition published = await definitions.PublishInProjectAsync(
                    RequestPrincipal.PrincipalOf(context), request.Project ?? string.Empty, request.DefinitionId, cancellationToken);
                return Results.Ok(DefinitionView.From(published));
            })
            .Produces<DefinitionView>();

        group.MapPost("/retire", async (HttpContext context, DefinitionService definitions, DefinitionIdRequest request, CancellationToken cancellationToken) =>
            {
                Definition retired = await definitions.RetireInProjectAsync(
                    RequestPrincipal.PrincipalOf(context), request.Project ?? string.Empty, request.DefinitionId, cancellationToken);
                return Results.Ok(DefinitionView.From(retired));
            })
            .Produces<DefinitionView>();

        return routes;
    }
}
