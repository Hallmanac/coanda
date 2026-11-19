using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coanda.Core.Flows;
using Coanda.Core.Registry;

namespace Coanda.Core.Triggers;

/// <summary>
/// The definition of a trigger: an event kind, equality filters on the event's fields, the target
/// that says what a matching event does, and, for a target that creates parcels, the most parcels it
/// creates in an hour. A trigger is data the daemon validates and never computes; the matching, the
/// firing, and the enforcing of the cap are the trigger loop's. A module's trigger and a user's are
/// the same shape.
/// </summary>
public sealed record TriggerDefinition
{
    public required string Name { get; init; }

    /// <summary>
    /// A plain name or a module-prefixed name (<c>&lt;module&gt;/&lt;name&gt;</c>), as an external
    /// event's kind is, or <see cref="TriggerEvent.FlowRunEndedKind"/>.
    /// </summary>
    public required string EventKind { get; init; }

    /// <summary>
    /// Event field names to the values they must equal exactly. Stored as written: a <c>flow</c> filter
    /// on a flow run's end is folded by the trigger loop when it matches, as a follow's flow name is.
    /// </summary>
    public IReadOnlyDictionary<string, string> Filters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public required TriggerTarget Target { get; init; }

    /// <summary>
    /// The most parcels the trigger creates in an hour; required of a target that creates parcels and
    /// refused on one that acts on the event's parcel. Left out of the JSON when absent.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HourlyCap { get; init; }

    /// <summary>
    /// Content hash of the definition, over its canonical form, in which the filters are taken in
    /// ordinal order of their field names, so listing them in another order does not change it.
    /// </summary>
    [JsonIgnore]
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Canonical(this))))[..16];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new DomainException("A trigger needs a name.");
        }

        RequireEventKind();

        if (Filters.FirstOrDefault(filter => !Flow.IsPlainName(filter.Key)) is { Key: { } notPlain })
        {
            throw new DomainException(
                $"Trigger '{Name}' filters on the field '{notPlain}'; a filter names an event field, which is a plain name (letters, digits, hyphens, and underscores).");
        }

        if (Filters.FirstOrDefault(filter => filter.Value is null) is { Key: { } valueless })
        {
            throw new DomainException($"Trigger '{Name}' filters on the field '{valueless}' with no value; a filter compares a field with a value.");
        }

        RequireTarget();

        if (string.Equals(EventKind, TriggerEvent.FlowRunEndedKind, StringComparison.Ordinal))
        {
            RequireFlowRunEnd();
        }

        RequireCap();
    }

    private void RequireEventKind()
    {
        if (string.Equals(EventKind, TriggerEvent.ParcelEnteredKind, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Trigger '{Name}' triggers on '{TriggerEvent.ParcelEnteredKind}', which a parcel's own creation and moves read as; a parcel entering a stage starts a flow through its type's flow binding, not through a trigger.");
        }

        if (!Flow.IsPlainName(EventKind) && !ModuleNames.IsPrefixed(EventKind))
        {
            throw new DomainException(
                $"Trigger '{Name}' needs an event kind that is a plain name (letters, digits, hyphens, and underscores), a module's name and a plain name joined by a slash (<module>/<name>), where a module's name is {ModuleNames.Rule}, or '{TriggerEvent.FlowRunEndedKind}'; '{EventKind}' is not.");
        }
    }

    private void RequireTarget()
    {
        switch (Target)
        {
            case null or { CreateParcel: null, EventParcel: null }:
                throw new DomainException(
                    $"Trigger '{Name}' has no target; a trigger's target either creates a parcel or acts on the parcel the event names.");
            case { CreateParcel: not null, EventParcel: not null }:
                throw new DomainException(
                    $"Trigger '{Name}' has a target that both creates a parcel and acts on the parcel the event names; a target does one or the other.");
            case { CreateParcel: { } create }:
                RequireNamed(create.ParcelType, "parcel type", "the parcel type it creates");
                RequireNamed(create.TitleField, "title field", "the event field whose value titles the parcel it creates");
                RequireNamed(create.Flow, "flow", "the flow it starts on the parcel it creates");
                if (!Flow.IsPlainName(create.TitleField))
                {
                    throw new DomainException(
                        $"Trigger '{Name}' titles the parcel it creates with the field '{create.TitleField}'; an event field is a plain name (letters, digits, hyphens, and underscores).");
                }

                break;
            case { EventParcel: { } onEvent }:
                RequireNamed(onEvent.Flow, "flow", "the flow it starts on the parcel the event names");
                break;
        }
    }

    private void RequireNamed(string? value, string missing, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"Trigger '{Name}' has a target that names no {missing}; its target names {what}.");
        }
    }

    /// <summary>
    /// A trigger on a flow run's end names the flow whose end it follows, creates a parcel, and starts
    /// a flow other than the one it follows: starting a flow on the same parcel is a flow's follow,
    /// which has a depth cap and a queue a trigger does not.
    /// </summary>
    private void RequireFlowRunEnd()
    {
        if (!Filters.TryGetValue(TriggerEvent.FlowField, out string? followed) || string.IsNullOrWhiteSpace(followed))
        {
            throw new DomainException(
                $"Trigger '{Name}' triggers on '{TriggerEvent.FlowRunEndedKind}' with no '{TriggerEvent.FlowField}' filter; a trigger on a flow run's end names the flow whose end it follows with a filter on the event's '{TriggerEvent.FlowField}' field.");
        }

        if (Target.CreateParcel is not { } create)
        {
            throw new DomainException(
                $"Trigger '{Name}' triggers on '{TriggerEvent.FlowRunEndedKind}' and acts on the parcel whose flow run ended; a trigger on a flow run's end creates a parcel, and starting a flow on the same parcel is a flow's follow.");
        }

        if (string.Equals(TriggerEvent.FlowKey(followed), TriggerEvent.FlowKey(create.Flow ?? string.Empty), StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Trigger '{Name}' follows the end of the flow '{followed}' and starts the flow '{create.Flow}', the same flow; a trigger on a flow run's end starts a flow other than the one whose end it follows.");
        }
    }

    private void RequireCap()
    {
        switch (Target)
        {
            case { CreateParcel: not null } when HourlyCap is not >= 1:
                throw new DomainException(
                    $"Trigger '{Name}' creates parcels and {(HourlyCap is { } given ? $"carries the hourly cap {given}" : "carries no hourly cap")}; a trigger that creates parcels carries an hourly cap that is a whole number of at least one.");
            case { EventParcel: not null } when HourlyCap is { } cap:
                throw new DomainException(
                    $"Trigger '{Name}' acts on the parcel the event names and carries the hourly cap {cap}; it creates no parcel for a cap to count, so it carries none.");
        }
    }

    /// <summary>The trigger with its filters in ordinal order of their field names, which is how it is written and hashed.</summary>
    private static TriggerDefinition Canonical(TriggerDefinition trigger) =>
        trigger with { Filters = new SortedDictionary<string, string>(trigger.Filters.ToDictionary(StringComparer.Ordinal), StringComparer.Ordinal) };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    public static string CanonicalJson(TriggerDefinition trigger) => JsonSerializer.Serialize(Canonical(trigger), WriteOptions);

    /// <summary>
    /// Reads a trigger from JSON; anything that is not a trigger is a rule broken. Its members may come
    /// in any order and its filters too, which <see cref="CanonicalJson"/> and <see cref="Hash"/> make
    /// one form.
    /// </summary>
    public static TriggerDefinition FromJson(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<TriggerDefinition>(content, ReadOptions)
                ?? throw new DomainException("The content is empty; it cannot be read as a trigger definition.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new DomainException($"The content cannot be read as a trigger definition: {exception.Message}");
        }
    }

    public override string ToString() => $"{Name}@{Hash}";
}
