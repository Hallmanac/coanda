using System.Text.Json;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// Where a dispatch stands, as its system step answers when the daemon asks after a restart: still
/// running, done with a result, paused with a reason and a lever, or unknown to the step, on which
/// the daemon dispatches it again under the same id. The private constructor closes the hierarchy.
/// </summary>
public abstract record SystemStepStatus
{
    private SystemStepStatus()
    {
    }

    /// <summary>The dispatch goes on; <paramref name="Progress"/> is free text for a person, never routed on.</summary>
    public sealed record Running(string? Progress = null) : SystemStepStatus;

    /// <summary>The dispatch finished, whatever its verdict; <paramref name="Result"/> is the document the step routes on.</summary>
    public sealed record Done(JsonElement Result) : SystemStepStatus;

    /// <summary>The dispatch stopped the flow run to wait; <paramref name="Lever"/> is what a person acts on.</summary>
    public sealed record Paused(string Reason, string Lever) : SystemStepStatus;

    /// <summary>The step does not know the dispatch.</summary>
    public sealed record Unknown : SystemStepStatus;
}
