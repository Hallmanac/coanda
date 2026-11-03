using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Daemon.Flights;
using Coanda.Daemon.Parcels;

namespace Coanda.Daemon.Actions;

/// <summary>
/// Moves the flight's parcel to the state its <c>state</c> argument names, through the parcel
/// service and as the flight's principal. A parcel already in that state is left alone, so
/// performing the step again after a restart moves nothing twice. A move the parcel's type
/// refuses parks the flight quoting the rule.
/// </summary>
public sealed class MoveParcelAction(ParcelService parcels) : IFlowAction
{
    public const string StateArgument = "state";

    public string Name => CoreActions.MoveParcel;

    public async ValueTask<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        string state = ActionArguments.RequireString(request.Step, StateArgument);
        Parcel parcel = await parcels.ShowAsync(request.Principal, request.Project, request.ParcelId, cancellationToken)
            ?? throw new DomainException($"No parcel {Ids.Short(request.ParcelId)} in project '{request.Project}'.");

        if (string.Equals(parcel.State, state, StringComparison.Ordinal))
        {
            return ActionOutcome.DoneWith(Name, (StateArgument, state), ("moved", false));
        }

        try
        {
            await parcels.MoveAsync(request.Principal, request.Project, request.ParcelId, state, cancellationToken);
        }
        catch (DomainException refused)
        {
            return new ActionOutcome.Parked(
                $"Step '{request.Step.Name}' could not move parcel {Ids.Short(request.ParcelId)} to '{state}': {refused.Message}",
                FlightLevers.PerformAgain(request.Step.Name));
        }

        return ActionOutcome.DoneWith(Name, (StateArgument, state), ("moved", true));
    }
}
