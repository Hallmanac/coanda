using System.Text.Json;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Sdk.Contract;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// One dispatch of a system step: the project, the flow run and its parcel, and the flow step, which
/// carries the step's name, the system step it names, and its arguments as the flow author wrote
/// them. <paramref name="Visit"/> is which visit to the step this is, counting from 1;
/// <paramref name="DispatchId"/> is the id the daemon minted and recorded on the flow run before
/// calling the step, kept when a restart dispatches the step again; <paramref name="Resumed"/> is
/// true when the flow run was resumed while this visit was its current step, which is the event a
/// wait step waits for; and <paramref name="WorkPath"/> is the flow run's work path
/// (<see cref="NodeHome.FlowRunWorkPath"/>), which a step that prepares a workspace populates. The
/// <paramref name="Principal"/> the step acts as and the <paramref name="Flow"/> ride along for the
/// daemon's own steps; a module never sees either (<see cref="ToContract"/>).
/// </summary>
public sealed record SystemStepDispatch(
    Principal Principal,
    string Project,
    Guid FlowRunId,
    Guid ParcelId,
    Flow Flow,
    FlowStep Step,
    bool Resumed,
    int Visit,
    Guid DispatchId,
    string WorkPath)
{
    /// <summary>The name of the system step dispatched.</summary>
    public string SystemStep => Step.SystemStep ?? string.Empty;

    /// <summary>The step's arguments, as the flow author wrote them.</summary>
    public IReadOnlyDictionary<string, JsonElement> Arguments => Step.Arguments;

    /// <summary>
    /// The module API's dispatch request for this dispatch: ids and the step's arguments only, with
    /// the system step's name as the step a module's describe lists, prefixed when an earlier flow version
    /// names one of the five steps unprefixed (<see cref="UnprefixedSoftwareSteps.CurrentName"/>). The principal, the flow, and the flow step's own
    /// name stay with the daemon.
    /// </summary>
    public DispatchRequest ToContract() =>
        new(Project, ParcelId, FlowRunId, UnprefixedSoftwareSteps.CurrentName(SystemStep), Visit, DispatchId, Resumed, WorkPath, Arguments);
}
