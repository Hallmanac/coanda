using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// Moves the flow run's parcel to the stage its <c>stage</c> argument names, through the parcel
/// service, as the flow run's principal and on behalf of the flow run, which is the only flow run that may
/// move a parcel it holds. A parcel already in that stage is left alone, so performing the step
/// again after a restart moves nothing twice. A move the parcel's type refuses pauses the flow run
/// quoting the rule.
/// </summary>
public sealed class MoveParcelSystemStep(ParcelService parcels) : IFlowSystemStep
{
    public const string StageArgument = "stage";

    public string Name => CoreSystemSteps.MoveParcel;

    public async ValueTask<SystemStepOutcome> PerformAsync(SystemStepRequest request, CancellationToken cancellationToken)
    {
        string stage = SystemStepArguments.RequireString(request.Step, StageArgument);
        Parcel parcel = await parcels.ShowAsync(request.Principal, request.Project, request.ParcelId, cancellationToken)
            ?? throw new DomainException($"No parcel {Ids.Short(request.ParcelId)} in project '{request.Project}'.");

        if (string.Equals(parcel.Stage, stage, StringComparison.Ordinal))
        {
            return SystemStepOutcome.DoneWith(Name, (StageArgument, stage), ("moved", false));
        }

        try
        {
            await parcels.MoveAsync(request.Principal, request.Project, request.ParcelId, stage, request.FlowRunId, cancellationToken);
        }
        catch (DomainException refused)
        {
            return new SystemStepOutcome.Paused(
                $"Step '{request.Step.Name}' could not move parcel {Ids.Short(request.ParcelId)} to '{stage}': {refused.Message}",
                FlowRunLevers.PerformAgain(request.Step.Name));
        }

        return SystemStepOutcome.DoneWith(Name, (StageArgument, stage), ("moved", true));
    }
}
