using Coanda.Sdk.Client;
using Coanda.Sdk.Contract;

namespace Coanda.Modules.Software.Steps;

/// <summary>
/// One dispatch as a step sees it: the dispatch request, which carries ids and the step's arguments
/// only, and the core's API, through which the step reads the rest as the software module. The flow
/// run the dispatch belongs to is read once, when a step first needs it: for the name of the flow
/// step it is in, which the step's reasons and levers name, or for the chain it belongs to.
/// </summary>
public sealed class StepContext(DispatchRequest request, CoreApiClient core)
{
    private CoreFlowRun? flowRun;

    public DispatchRequest Request { get; } = request;

    public CoreApiClient Core { get; } = core;

    /// <summary>The flow run the dispatch belongs to, as the core shows it.</summary>
    public async Task<CoreFlowRun> FlowRunAsync(CancellationToken cancellationToken) =>
        flowRun ??= await Core.FlowRunAsync(Request.Project, Request.FlowRunId, cancellationToken);

    /// <summary>The name of the flow step the dispatch performs: the flow run's current step.</summary>
    public async Task<string> StepNameAsync(CancellationToken cancellationToken) =>
        (await FlowRunAsync(cancellationToken)).CurrentStep;

    /// <summary>A pause whose reason opens with the flow step's name and whose lever performs it again.</summary>
    public async Task<DispatchAnswer> PerformAgainAsync(string what, CancellationToken cancellationToken)
    {
        string step = await StepNameAsync(cancellationToken);
        return new DispatchAnswer.Paused($"Step '{step}' {what}", Levers.PerformAgain(step));
    }
}
