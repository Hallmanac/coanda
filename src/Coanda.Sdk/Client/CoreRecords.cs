using System.Text.Json;

namespace Coanda.Sdk.Client;

/// <summary>
/// A registered project as the core's API shows it: its repository's remote and primary branch, the
/// build and test commands its gates run, the defaults included, and the checks a merge waits for.
/// </summary>
public sealed record CoreProject(string Name, string Remote, string PrimaryBranch, string Build, string Test, IReadOnlyList<string> RequiredChecks);

/// <summary>A parcel as the core's API shows it, with every field the type version it was created under declares.</summary>
public sealed record CoreParcel(Guid Id, string Project, string Type, string Stage, string Title, Guid? LiveFlowRunId, IReadOnlyList<CoreParcelField> Fields)
{
    /// <summary>The value the parcel holds in the field <paramref name="name"/>, or null when it holds none or its type declares no such field.</summary>
    public CoreFieldValue? ValueOf(string name) => Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))?.Value;
}

/// <summary>A field a parcel's type version declares, with the value the parcel holds in it, or null when it holds none.</summary>
public sealed record CoreParcelField(string Name, string Kind, CoreFieldValue? Value);

/// <summary>A value of one of a parcel's fields: exactly one of its text, its items, and its reference, the one the field's kind names.</summary>
public sealed record CoreFieldValue(string? Text = null, IReadOnlyList<string>? Items = null, CoreFieldReference? Reference = null)
{
    public static CoreFieldValue OfReference(string id, string? url = null) => new(Reference: new CoreFieldReference(id, url));
}

/// <summary>A reference a field holds: the id of something outside Coanda and, when it has one, its URL.</summary>
public sealed record CoreFieldReference(string Id, string? Url = null);

/// <summary>
/// A flow run as the core's API shows it. <see cref="Status"/> is running, paused, ended, or stopped,
/// <see cref="CurrentStep"/> is the name of the step it is in, and every flow run of a chain carries the
/// chain's <see cref="RootFlowRunId"/>.
/// </summary>
public sealed record CoreFlowRun(
    Guid Id,
    string Project,
    Guid ParcelId,
    string FlowName,
    string Status,
    string CurrentStep,
    Guid RootFlowRunId,
    IReadOnlyList<CoreFlowRunStep> Steps)
{
    /// <summary>Whether the flow run has ended or been stopped, after which it never runs again.</summary>
    public bool IsFinal => Status is "ended" or "stopped";
}

/// <summary>
/// One step a flow run entered: its name, its <see cref="Kind"/> (agent-step or system-step), the agent or
/// system step it names as <see cref="Performer"/>, and, for a system step, its result once recorded.
/// </summary>
public sealed record CoreFlowRunStep(string Step, string Kind, string Performer, JsonElement? Result, DateTimeOffset? RecordedAt);

/// <summary>The core's API refused a call or could not find what it named; the message quotes the core's own reason.</summary>
public sealed class CoreApiException(int status, string message) : Exception(message)
{
    /// <summary>The HTTP status the core answered with.</summary>
    public int Status { get; } = status;
}
