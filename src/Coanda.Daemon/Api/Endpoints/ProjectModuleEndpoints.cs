using Coanda.Daemon.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Coanda.Daemon.Api.Endpoints;

/// <summary>
/// A module's settings in a project. The project and the module are named in the query or the body, never
/// in a path segment of the module's own, so no route names a module. Every call answers with the module's
/// settings as they stand after it.
/// </summary>
public static class ProjectModuleEndpoints
{
    public const string Path = "/project-modules";

    /// <summary>The path that shows the module's settings in the project.</summary>
    public static string ShowPathOf(string project, string module) =>
        $"{Path}?project={Uri.EscapeDataString(project)}&module={Uri.EscapeDataString(module)}";

    public static IEndpointRouteBuilder MapProjectModuleEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup(Path);

        group.MapGet("", async (HttpContext context, ModuleSettingsService modules, string project, string module, CancellationToken cancellationToken) =>
                Results.Ok(await modules.ShowAsync(RequestPrincipal.PrincipalOf(context), project, module, cancellationToken)))
            .Produces<ModuleSettingsShown>();

        group.MapPost("/settings", async (HttpContext context, ModuleSettingsService modules, ProjectModuleSetRequest request, CancellationToken cancellationToken) =>
                Results.Ok(await modules.SetAsync(
                    RequestPrincipal.PrincipalOf(context), request.Project, request.Module, request.Name, request.Value, cancellationToken)))
            .Produces<ModuleSettingsShown>();

        group.MapPost("/profile", async (HttpContext context, ModuleSettingsService modules, ProjectModuleSelectProfileRequest request, CancellationToken cancellationToken) =>
                Results.Ok(await modules.SelectProfileAsync(
                    RequestPrincipal.PrincipalOf(context), request.Project, request.Module, request.Profile, cancellationToken)))
            .Produces<ModuleSettingsShown>();

        return routes;
    }
}
