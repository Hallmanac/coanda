using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Api;
using Coanda.Daemon.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>Publishes a project-scope flow or parcel type through the running daemon's definition service, as its owner.</summary>
public static class Publishing
{
    public static async Task<Flow> FlowAsync(RunningDaemon daemon, string project, Flow flow)
    {
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, project, flow.Name,
            Flow.CanonicalJson(flow), flow.Hash, null, CancellationToken.None);
        await definitions.PublishAsync(Principal.LocalOwner, project, draft.Id, CancellationToken.None);
        return flow;
    }

    /// <summary>Publishes <paramref name="type"/> as a project-scope parcel type of <paramref name="project"/>, with no fork base.</summary>
    public static async Task<ParcelType> TypeAsync(RunningDaemon daemon, string project, ParcelType type)
    {
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, project, type, null, CancellationToken.None);
        await definitions.PublishAsync(Principal.LocalOwner, project, draft.Id, CancellationToken.None);
        return type;
    }
}
