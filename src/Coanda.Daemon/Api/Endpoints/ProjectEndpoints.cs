using Coanda.Core.Projects;
using Coanda.Daemon.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Coanda.Daemon.Api.Endpoints;

public static class ProjectEndpoints
{
    /// <summary>
    /// The path that shows the named project. Each segment is escaped and the slashes are kept,
    /// because the show route takes the rest of the path and routing never decodes an escaped slash.
    /// </summary>
    public static string PathOf(string name) =>
        $"/projects/{string.Join('/', name.Split('/').Select(Uri.EscapeDataString))}";

    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/projects");

        group.MapGet("", async (HttpContext context, ProjectService projects, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<Project> list = await projects.ListAsync(RequestPrincipal.PrincipalOf(context), cancellationToken);
                List<ProjectView> views = [.. list.Select(ProjectView.From)];
                return Results.Ok(views);
            })
            .Produces<List<ProjectView>>();

        group.MapGet("/{**name}", async (HttpContext context, ProjectService projects, string name, CancellationToken cancellationToken) =>
            {
                Project? project = await projects.ShowAsync(RequestPrincipal.PrincipalOf(context), name, cancellationToken);
                return project is null
                    ? Results.NotFound(new ErrorView(ProjectService.Unregistered(name.Trim())))
                    : Results.Ok(ProjectView.From(project));
            })
            .Produces<ProjectView>()
            .Produces<ErrorView>(StatusCodes.Status404NotFound);

        group.MapPost("", async (HttpContext context, ProjectService projects, ProjectAddRequest request, CancellationToken cancellationToken) =>
            {
                Project project = await projects.AddAsync(
                    RequestPrincipal.PrincipalOf(context), request.Name, request.Remote, request.PrimaryBranch, cancellationToken);
                return Results.Created(PathOf(project.Name), ProjectView.From(project));
            })
            .Produces<ProjectView>(StatusCodes.Status201Created);

        return routes;
    }
}
