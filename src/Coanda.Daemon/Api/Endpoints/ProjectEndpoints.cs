using Coanda.Core.Projects;
using Coanda.Core.Registry;
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

    /// <summary>The segment after a project's path that names its required checks.</summary>
    public const string RequiredChecksSegment = "required-checks";

    /// <summary>The path that replaces the named project's required checks: its show path, then <see cref="RequiredChecksSegment"/>.</summary>
    public static string RequiredChecksPathOf(string name) => $"{PathOf(name)}/{RequiredChecksSegment}";

    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/projects");

        group.MapGet("", async (HttpContext context, ProjectService projects, CancellationToken cancellationToken) =>
            {
                Principal principal = RequestPrincipal.PrincipalOf(context);
                IReadOnlyList<Project> list = await projects.ListAsync(principal, cancellationToken);
                IReadOnlyDictionary<string, string> profiles = await projects.ProfilesAsync(principal, cancellationToken);
                List<ProjectView> views = [.. list.Select(p => ProjectView.From(p, profiles.GetValueOrDefault(p.Name)))];
                return Results.Ok(views);
            })
            .Produces<List<ProjectView>>();

        group.MapGet("/{**name}", async (HttpContext context, ProjectService projects, string name, CancellationToken cancellationToken) =>
            {
                Principal principal = RequestPrincipal.PrincipalOf(context);
                Project? project = await projects.ShowAsync(principal, name, cancellationToken);
                return project is null
                    ? Results.NotFound(new ErrorView(ProjectService.Unregistered(name.Trim())))
                    : Results.Ok(ProjectView.From(project, await projects.ProfileOfAsync(principal, name, cancellationToken)));
            })
            .Produces<ProjectView>()
            .Produces<ErrorView>(StatusCodes.Status404NotFound);

        group.MapPost("", async (HttpContext context, ProjectService projects, ProjectAddRequest request, CancellationToken cancellationToken) =>
            {
                Project project = await projects.AddAsync(
                    RequestPrincipal.PrincipalOf(context), request.Name, request.Remote, request.PrimaryBranch, request.Build, request.Test, cancellationToken);
                return Results.Created(PathOf(project.Name), ProjectView.From(project, null));
            })
            .Produces<ProjectView>(StatusCodes.Status201Created);

        // PUT /projects/{name}/required-checks. A project's name may hold a slash, and a catch-all
        // segment must come last, so the route takes the rest of the path and the last segment is read off it.
        group.MapPut("/{**path}", async (HttpContext context, ProjectService projects, string path, ProjectRequiredChecksRequest request, CancellationToken cancellationToken) =>
            {
                string suffix = $"/{RequiredChecksSegment}";
                if (!path.EndsWith(suffix, StringComparison.Ordinal) || path.Length == suffix.Length)
                {
                    return Results.NotFound(new ErrorView($"PUT /projects/{path} names nothing; PUT /projects/<name>/{RequiredChecksSegment} replaces a project's required checks."));
                }

                Principal principal = RequestPrincipal.PrincipalOf(context);
                Project project = await projects.SetRequiredChecksAsync(principal, path[..^suffix.Length], request.Checks, cancellationToken);
                return Results.Ok(ProjectView.From(project, await projects.ProfileOfAsync(principal, project.Name, cancellationToken)));
            })
            .Produces<ProjectView>()
            .Produces<ErrorView>(StatusCodes.Status404NotFound);

        // A fixed path with the project in the body, since the show route already takes the rest of /projects/.
        group.MapPost("/profile", async (HttpContext context, ProjectService projects, ProjectSetProfileRequest request, CancellationToken cancellationToken) =>
            {
                Principal principal = RequestPrincipal.PrincipalOf(context);
                ProjectProfile setting = await projects.SetProfileAsync(principal, request.Project, request.Profile, cancellationToken);
                Project project = await projects.ShowAsync(principal, setting.Project, cancellationToken)
                    ?? throw new InvalidOperationException($"Project '{setting.Project}' named a profile but has no record.");
                return Results.Ok(ProjectView.From(project, setting.Profile));
            })
            .Produces<ProjectView>();

        return routes;
    }
}
