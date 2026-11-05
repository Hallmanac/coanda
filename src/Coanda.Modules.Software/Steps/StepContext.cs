using Coanda.Sdk.Client;
using Coanda.Sdk.Contract;

namespace Coanda.Modules.Software.Steps;

/// <summary>
/// One dispatch as a step sees it: the dispatch request, which carries ids and the step's arguments
/// only, and the core's API, through which the step reads the rest as the software module. The flow
/// run the dispatch belongs to, the parcel, and the module's settings in the project are each read
/// once, when a step first needs them: the flow run for the name of the flow step it is in, which the
/// step's reasons and levers name, or for the chain it belongs to.
/// </summary>
public sealed class StepContext(DispatchRequest request, CoreApiClient core)
{
    private CoreFlowRun? flowRun;
    private CoreParcel? parcel;
    private CoreModuleSettings? settings;

    public DispatchRequest Request { get; } = request;

    public CoreApiClient Core { get; } = core;

    /// <summary>The flow run the dispatch belongs to, as the core shows it.</summary>
    public async Task<CoreFlowRun> FlowRunAsync(CancellationToken cancellationToken) =>
        flowRun ??= await Core.FlowRunAsync(Request.Project, Request.FlowRunId, cancellationToken);

    /// <summary>The parcel the dispatch acts for, as the core showed it when it was first read or last written.</summary>
    public async Task<CoreParcel> ParcelAsync(CancellationToken cancellationToken) =>
        parcel ??= await Core.ParcelAsync(Request.Project, Request.ParcelId, cancellationToken);

    /// <summary>The module's settings in the project, as the core shows them.</summary>
    public async Task<CoreModuleSettings> SettingsAsync(CancellationToken cancellationToken) =>
        settings ??= await Core.ModuleSettingsAsync(Request.Project, SoftwareSteps.Module, cancellationToken);

    /// <summary>
    /// Records <paramref name="value"/> in the parcel's field <paramref name="field"/> under the dispatch,
    /// and keeps the parcel as the write left it.
    /// </summary>
    public async Task<CoreParcel> SetFieldAsync(string field, CoreFieldValue value, CancellationToken cancellationToken) =>
        parcel = await Core.SetFieldAsync(Request.Project, Request.ParcelId, field, value, Request.FlowRunId, Request.DispatchId, cancellationToken);

    /// <summary>
    /// Records <paramref name="value"/> in the parcel's field <paramref name="field"/>, one the module owns
    /// and sets under no dispatch, and keeps the parcel as the write left it.
    /// </summary>
    public async Task<CoreParcel> SetOwnedFieldAsync(string field, CoreFieldValue value, CancellationToken cancellationToken) =>
        parcel = await Core.SetOwnedFieldAsync(Request.Project, Request.ParcelId, field, value, cancellationToken);

    /// <summary>The name of the flow step the dispatch performs: the flow run's current step.</summary>
    public async Task<string> StepNameAsync(CancellationToken cancellationToken) =>
        (await FlowRunAsync(cancellationToken)).CurrentStep;

    /// <summary>A pause whose reason opens with the flow step's name and whose lever performs it again.</summary>
    public async Task<DispatchAnswer> PerformAgainAsync(string what, CancellationToken cancellationToken)
    {
        string step = await StepNameAsync(cancellationToken);
        return new DispatchAnswer.Paused($"Step '{step}' {what}", Levers.PerformAgain(step));
    }

    /// <summary>A pause whose reason opens with the flow step's name and whose lever is <paramref name="lever"/> for it.</summary>
    public async Task<DispatchAnswer> PausedAsync(string what, Func<string, string> lever, CancellationToken cancellationToken)
    {
        string step = await StepNameAsync(cancellationToken);
        return new DispatchAnswer.Paused($"Step '{step}' {what}", lever(step));
    }
}
