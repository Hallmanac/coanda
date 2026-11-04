using Coanda.Core.Definitions;
using Coanda.Core.Contours;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>Publishes a project-scope contour through the running daemon's definition service, as its owner.</summary>
public static class Publishing
{
    public static async Task<Contour> ContourAsync(RunningDaemon daemon, string project, Contour contour)
    {
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Contour, DefinitionScope.Project, project, contour.Name,
            Contour.CanonicalJson(contour), contour.Hash, null, CancellationToken.None);
        await definitions.PublishAsync(Principal.LocalOwner, project, draft.Id, CancellationToken.None);
        return contour;
    }
}
