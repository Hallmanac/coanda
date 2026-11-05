using System.Text.Json;

namespace Coanda.Sdk.Client;

/// <summary>A registered project as the core's API shows it: its name and its repos.</summary>
public sealed record CoreProject(string Name, IReadOnlyList<CoreRepo> Repos);

/// <summary>One of a project's repos: the name it goes by, which is one plain directory name, and its git remote.</summary>
public sealed record CoreRepo(string Name, string Remote);

/// <summary>
/// A module's settings in a project as the core's API shows them: each replicated setting the module
/// declares with where its value comes from (<c>Set</c>, <c>Default</c>, or <c>Unset</c>) and the value,
/// the default's when it is the default, or null when there is none; and its credential profile setting
/// on this node, when it declares one.
/// </summary>
public sealed record CoreModuleSettings(string Project, string Module, IReadOnlyList<CoreReplicatedSetting> Replicated, CoreCredentialProfile? CredentialProfile)
{
    /// <summary>The value the replicated setting <paramref name="name"/> has, its default included, or null when it has none or the module declares no such setting.</summary>
    public JsonElement? ValueOf(string name) =>
        Replicated.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal)) is { Value: { ValueKind: not JsonValueKind.Null } value }
            ? value
            : null;
}

/// <summary>One replicated setting a module declares, with its value in the project and where that comes from.</summary>
public sealed record CoreReplicatedSetting(string Name, bool Required, string Source, JsonElement? Value);

/// <summary>
/// The credential profile setting of a module in a project on this node: <see cref="Source"/> is
/// <c>Set</c> when a profile the node holds is selected, <c>Missing</c> when the profile selected is one
/// it no longer holds, <c>Default</c> when none is selected and the node's default identity stands, and
/// <c>Unset</c> when none is selected and nothing stands; <see cref="Profile"/> is the selected profile's name.
/// </summary>
public sealed record CoreCredentialProfile(bool Required, string Source, string? Profile)
{
    /// <summary>Whether a profile this node holds is selected.</summary>
    public bool IsSelected => string.Equals(Source, "Set", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether no profile is selected and the node's default identity stands in for one.</summary>
    public bool IsNodeDefault => string.Equals(Source, "Default", StringComparison.OrdinalIgnoreCase);
}

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
/// chain's <see cref="RootFlowRunId"/>. <see cref="Working"/> is true while the core has a worker
/// advancing the flow run or a person's stop of it being saved; a core that does not say is taken as
/// working, the side that never acts on a flow run something may still be running for.
/// <see cref="WorkPath"/> is the flow run's work path as the core places it, the path every dispatch of
/// it carries, or null when the core gives it none; a module never computes it.
/// </summary>
public sealed record CoreFlowRun(
    Guid Id,
    string Project,
    Guid ParcelId,
    string FlowName,
    string Status,
    string CurrentStep,
    Guid RootFlowRunId,
    IReadOnlyList<CoreFlowRunStep> Steps,
    bool Working = true,
    string? WorkPath = null)
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
