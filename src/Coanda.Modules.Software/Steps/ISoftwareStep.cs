using Coanda.Sdk.Contract;

namespace Coanda.Modules.Software.Steps;

/// <summary>
/// One of the module's steps. A dispatch reads the step's arguments and never evaluates them, pulls
/// what else it needs through the core's API, and answers done with a result its describe declares, or
/// paused with a reason and a lever. Dispatching again under the same dispatch id must be safe, and the
/// step makes it so by checking the world. None of the steps answers running, so none keeps a record
/// of its dispatches.
/// </summary>
public interface ISoftwareStep
{
    /// <summary>The step's prefixed name, as describe lists it.</summary>
    string Name { get; }

    Task<DispatchAnswer> DispatchAsync(StepContext context, CancellationToken cancellationToken);
}
