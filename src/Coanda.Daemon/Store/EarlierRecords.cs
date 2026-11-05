using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Projects;
using Coanda.Daemon.SystemSteps;
using Coanda.Sdk.Contract;
using Fisher;
using JasperFx.Events.Upcasting;
using Weasel.Core;

namespace Coanda.Daemon.Store;

/// <summary>
/// Every shape an earlier build wrote into a node home that this build still reads, and how each is
/// read as its current shape. Nothing here rewrites a stored event: an earlier event is upgraded as
/// it is read (<see cref="Upgrade"/>, which the store's upcasting registry runs on every event read),
/// an earlier snapshot document is upgraded as it is deserialized, and an event this build appends is
/// put in its current shape before it is appended (<see cref="Current{TEvent}"/>). The core reads only
/// current shapes.
/// <para>The shapes upgraded here:</para>
/// <list type="number">
/// <item>A <c>definition_drafted</c> in the scope stored as <c>Shipped</c>, with no module and no module
/// version, last written by the build before 6b895be (2026-11-05), which brought module scope in. It
/// reads as module scope, module <c>software</c>, version <c>0.1.0</c>: in the event through
/// <see cref="Upgrade"/>, and in a stored definition document through the store's document read.</item>
/// <item>A flow version whose content names one of the five software steps unprefixed (worktree, gates,
/// open-pr, gated-merge, rebase), last written by the build before 80772aa (2026-11-05), which carved
/// the software steps into a module. It keeps its content and its content hash, since flow runs
/// recorded that hash and a flow run keeps the flow it started under; the step names it holds are read
/// through <see cref="CurrentStepName"/> wherever the daemon dispatches, asks the status of, stops, or
/// checks the arguments of a step.</item>
/// <item>A flow run event whose performer is one of the five unprefixed names
/// (<c>flow_run_started</c>, <c>step_started</c>, <c>system_step_dispatched</c>,
/// <c>system_step_completed</c>), last written by the build at c6d62d3 (2026-11-05) for a flow run
/// under such a flow version. It reads with <c>software/&lt;name&gt;</c>, in the event through
/// <see cref="Upgrade"/> and in a stored flow run document's visits through the store's document
/// read, and this build appends the prefixed name (<see cref="Current{TEvent}"/>).</item>
/// <item>The open-pr verdict a flow from before the build flow ended at the open pull request routes
/// on, last written by the build before e3c770d (2026-11-04): such a flow routes open-pr on
/// <c>done</c> alone, which open-pr answered before it answered <c>opened</c>, and
/// <see cref="OpenPrResult"/> answers it <c>done</c> on the dispatch path and the status path.</item>
/// <item>The project record from before repos and module settings, last written by the build before
/// 48b326e (2026-11-05), which named one remote, its primary branch, and the build and test commands
/// in the registration and the merge's checks in an event of its own.
/// <see cref="EarlierProjectRecords"/> carries it into the software module's settings once, at the
/// daemon's start.</item>
/// </list>
/// <para>Earlier shapes still read elsewhere, which this type lists and does not hold:</para>
/// <list type="bullet">
/// <item>A <c>project_registered</c> that carries a remote in place of repos, last written by the build
/// before 48b326e (2026-11-05), read by <c>ProjectRegistered.ReposRecorded</c> in the core, and written
/// by the ledger export with its repo and without its remote (<c>Transport.LedgerExport</c>).</item>
/// <item>A fork base of a name and a store-given version, last written by the build before 4faa812
/// (2026-11-05), read through <see cref="ForkBase.Earlier"/> and <see cref="ForkBase.IsEarlierShape"/>
/// in the core, and written by the ledger export resolved on this node to the current shape.</item>
/// <item>A <c>DeferredFollows</c> document whose pending follow carries no closes flag, last written by
/// the build before 10cf9d1 (2026-11-04), read through <c>PendingFollow.Closes</c> defaulting to
/// false in the daemon.</item>
/// </list>
/// <para>
/// An <c>agent_step_spawned</c> with no agent hash and model spec, which builds before 996e069
/// (2026-11-03) wrote, or with no launch id, which builds before 3e72284 (2026-11-04) wrote, is not an
/// earlier shape: those members are nullable in the core's <c>AgentStepSpawned</c> for such records, and
/// there is no value to upgrade them to, so it is a current shape, which the ledger export writes and its
/// import accepts as the type holds it (the owner's ruling of 2026-11-05).
/// </para>
/// <para>
/// The ledger import refuses a line in an earlier shape (<see cref="EarlierShapeOf"/>), since once stored
/// it would read as current and nothing could tell it apart.
/// </para>
/// <para>
/// An export of the record (<see cref="RecordExport"/>) writes every event in its current shape through
/// <see cref="Exported"/>, which puts the shapes read elsewhere in theirs too, and a deferred follow in its
/// current shape through <see cref="CurrentDeferredFollows"/>; a rebuild from the export holds no earlier
/// shape but the two that stay current, flow content naming the software steps unprefixed and an
/// <c>agent_step_spawned</c> with null members.
/// </para>
/// <para>
/// This type, and each reader listed above as elsewhere, is deleted in the change that passes phase 3's
/// gate, once the node has been rebuilt from an export, which emits only current shapes. From then on the
/// daemon refuses a home holding any of these shapes, as <see cref="PreRenameStore"/> refuses a home
/// written before the rename.
/// </para>
/// </summary>
public static class EarlierRecords
{
    /// <summary>The module the five unprefixed steps, and every definition recorded in shipped scope, belong to.</summary>
    public const string SoftwareModule = "software";

    /// <summary>The module version a definition recorded in shipped scope reads as: the version whose manifest first carried those definitions as files.</summary>
    public const string SoftwareModuleVersion = "0.1.0";

    /// <summary>The name module scope was stored under before it took the place of shipped scope.</summary>
    public const string ShippedScope = "Shipped";

    /// <summary>The five system steps every build before the carve-out named without the software module's prefix.</summary>
    public static readonly IReadOnlyList<string> UnprefixedSteps = ["worktree", "gates", "open-pr", "gated-merge", "rebase"];

    private const string ProjectRegistered = "project_registered";
    private const string ProjectRequiredChecksSet = "project_required_checks_set";
    private const string DefinitionDraftedType = "definition_drafted";

    /// <summary>What a registration from before repos and module settings held beside the remote, and this build no longer records.</summary>
    private static readonly IReadOnlyList<string> EarlierRegistrationMembers = ["remote", "primaryBranch", "buildCommand", "testCommand"];

    private const string UnprefixedOpenPr = "open-pr";
    private const string OpenedVerdict = "opened";

    private static readonly IReadOnlyDictionary<string, Type> EventTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["definition_drafted"] = typeof(DefinitionDrafted),
        ["flow_run_started"] = typeof(FlowRunStarted),
        ["step_started"] = typeof(StepStarted),
        ["system_step_dispatched"] = typeof(SystemStepDispatched),
        ["system_step_completed"] = typeof(SystemStepCompleted),
    };

    /// <summary>
    /// The prefixed name of <paramref name="systemStep"/> when it is one of the five
    /// <see cref="UnprefixedSteps"/>, and <paramref name="systemStep"/> as it is otherwise.
    /// </summary>
    public static string CurrentStepName(string systemStep) =>
        UnprefixedSteps.Contains(systemStep, StringComparer.Ordinal)
            ? $"{SoftwareModule}/{systemStep}"
            : systemStep;

    /// <summary>
    /// One stored event, given as the events table holds it (its type name and its JSON data), in its
    /// current shape. An event already in its current shape comes back as it was given. A definition's
    /// content and content hash are left as stored.
    /// </summary>
    public static StoredEvent Upgrade(StoredEvent stored)
    {
        if (!EventTypes.ContainsKey(stored.Type) || JsonNode.Parse(stored.Data) is not JsonObject data)
        {
            return stored;
        }

        bool upgraded = stored.Type switch
        {
            "definition_drafted" => UpgradeDefinition(data),
            "flow_run_started" => UpgradePerformer(data, "firstStepKind", "firstStepPerformer"),
            "step_started" => UpgradePerformer(data, "kind", "performer"),
            "system_step_dispatched" or "system_step_completed" => UpgradePerformer(data, kind: null, "systemStep"),
            _ => false,
        };
        return upgraded
            ? stored with { Data = data.ToJsonString() }
            : stored;
    }

    /// <summary>
    /// One stored event, given as the events table holds it, in the shape an export writes, or null when
    /// an export leaves it out. It is <see cref="Upgrade"/>'s current shape with the shapes this build
    /// reads elsewhere put in theirs too: a <c>project_registered</c> from before repos is written with its
    /// one repo and without the remote, the primary branch, and the build and test commands, and a
    /// <c>project_required_checks_set</c> is left out, since <see cref="EarlierProjectRecords"/> carries what
    /// both held into the software module's settings; and a fork base of a name and a store-given version
    /// is written as the base <paramref name="currentBase"/> gives for the definition's kind, name, and that
    /// version, which is the module-scope definition it resolves to on this node. A fork base that resolves
    /// to none is refused, naming the definition. A definition's content and content hash are left as
    /// stored, and so is an <c>agent_step_spawned</c> with null members, which is a current shape.
    /// </summary>
    public static StoredEvent? Exported(StoredEvent stored, Func<DefinitionKind, string, int, ForkBase?> currentBase)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(currentBase);
        if (stored.Type == ProjectRequiredChecksSet)
        {
            return null;
        }

        StoredEvent current = Upgrade(stored);
        if (current.Type is not (ProjectRegistered or DefinitionDraftedType) || JsonNode.Parse(current.Data) is not JsonObject data)
        {
            return current;
        }

        bool changed = current.Type == ProjectRegistered
            ? ExportRegistration(data)
            : ExportForkBase(data, currentBase);
        return changed
            ? current with { Data = data.ToJsonString() }
            : current;
    }

    /// <summary>
    /// Why <paramref name="stored"/>, an event a peer's line carries, is in an earlier shape, or null when it
    /// is in its current shape. Earlier are: an event <see cref="Upgrade"/> would change; a
    /// <c>project_registered</c> carrying any member of the registration from before repos (the remote, the
    /// primary branch, or the build or test command), whether or not it also carries repos; and a
    /// <c>definition_drafted</c> whose fork base is a name and a store-given version with no content hash. A
    /// definition's content naming a software step unprefixed is current, since its content and content hash
    /// are never changed, and so is an <c>agent_step_spawned</c> with null members. Whether this build has an
    /// event type of the line's type name at all is the importer's to ask.
    /// </summary>
    public static string? EarlierShapeOf(StoredEvent stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (!ReferenceEquals(Upgrade(stored), stored))
        {
            return $"its '{stored.Type}' is in a shape an earlier build wrote, which this build upgrades as it reads it";
        }

        JsonObject? data = stored.Type is ProjectRegistered or DefinitionDraftedType
            ? JsonNode.Parse(stored.Data) as JsonObject
            : null;
        return (stored.Type, data) switch
        {
            (ProjectRegistered, { } registration) when EarlierRegistrationMembers.FirstOrDefault(registration.ContainsKey) is { } member =>
                $"its registration carries '{member}', a member of the registration from before repos",
            (DefinitionDraftedType, { } drafted) when drafted["forkBase"] is JsonObject forkBase && !IsText(forkBase["contentHash"]) =>
                "its fork base is a name and a store-given version, the shape from before fork bases named a content hash",
            _ => null,
        };
    }

    /// <summary>
    /// A stored <c>DeferredFollows</c> document's JSON in its current shape: a pending follow recorded
    /// before the closes flag existed carries it as false, which is how it reads. Any other JSON is
    /// returned as it was given.
    /// </summary>
    public static string CurrentDeferredFollows(string data)
    {
        if (JsonNode.Parse(data) is not JsonObject document || document["pending"] is not JsonArray pending)
        {
            return data;
        }

        bool changed = false;
        foreach (JsonObject follow in pending.OfType<JsonObject>().Where(f => !f.ContainsKey("closes")))
        {
            follow["closes"] = false;
            changed = true;
        }

        return changed
            ? document.ToJsonString()
            : data;
    }

    /// <summary>
    /// <paramref name="recorded"/>, an event this build is about to append, in its current shape: a flow run
    /// event naming one of the five steps unprefixed, which a flow run under a flow version from before the
    /// carve-out decides, names it prefixed. Every other event is returned as it is.
    /// </summary>
    public static TEvent Current<TEvent>(TEvent recorded)
        where TEvent : notnull =>
        recorded switch
        {
            FlowRunStarted { FirstStepKind: StepKind.SystemStep } started => (TEvent)(object)(started with { FirstStepPerformer = CurrentStepName(started.FirstStepPerformer) }),
            StepStarted { Kind: StepKind.SystemStep } started => (TEvent)(object)(started with { Performer = CurrentStepName(started.Performer) }),
            SystemStepDispatched dispatched => (TEvent)(object)(dispatched with { SystemStep = CurrentStepName(dispatched.SystemStep) }),
            SystemStepCompleted completed => (TEvent)(object)(completed with { SystemStep = CurrentStepName(completed.SystemStep) }),
            _ => recorded,
        };

    /// <summary>
    /// The result a flow run records for <paramref name="result"/>, which a module answered for the flow step
    /// <paramref name="step"/>. A flow version written before the build flow ended at the open pull request
    /// names open-pr unprefixed and routes it on <c>done</c> alone, which open-pr answered before it answered
    /// <c>opened</c>; such a step's <c>opened</c> is answered <c>done</c>. A step naming
    /// <c>software/open-pr</c>, or routing on <c>opened</c>, gets the verdict the module answered.
    /// </summary>
    public static StepResult OpenPrResult(FlowStep step, StepResult result) =>
        step.SystemStep == UnprefixedOpenPr
        && result.Verdict == OpenedVerdict
        && RoutesOn(step, SystemStepAnswer.DoneVerdict)
        && !RoutesOn(step, OpenedVerdict)
            ? result with { Verdict = SystemStepAnswer.DoneVerdict }
            : result;

    /// <summary>
    /// Has the store read every earlier event through <see cref="Upgrade"/>, by its upcasting registry, and
    /// every earlier definition and flow run document in its current shape, by its serializer.
    /// </summary>
    public static void Configure(StoreOptions options)
    {
        foreach ((string name, Type type) in EventTypes)
        {
            options.Events.Upcasters.Register(new UpcastTransformation(type, name, payload =>
            {
                using JsonDocument document = payload.AsJsonDocument();
                StoredEvent current = Upgrade(new StoredEvent(name, document.RootElement.GetRawText()));
                return options.Serializer.FromJson(type, current.Data);
            }));
        }

        if (options.Serializer is SystemTextJsonSerializer serializer)
        {
            serializer.Options.Converters.Add(new UpgradingConverter<Definition>(UpgradeDefinition));
            serializer.Options.Converters.Add(new UpgradingConverter<FlowRunStepEntry>(visit => UpgradePerformer(visit, "kind", "performer")));
        }
        else
        {
            throw new InvalidOperationException("The store reads earlier documents through its System.Text.Json serializer, and was given another.");
        }
    }

    /// <summary>A registration from before repos: its remote as its one repo, and nothing of the software module's.</summary>
    private static bool ExportRegistration(JsonObject registration)
    {
        bool changed = false;
        if (registration["repos"] is null && registration["remote"] is JsonValue remote && remote.GetValueKind() == JsonValueKind.String)
        {
            ProjectRepo repo = ProjectRepo.FromRemote(remote.GetValue<string>());
            registration["repos"] = new JsonArray(new JsonObject { ["name"] = repo.Name, ["remote"] = repo.Remote });
            changed = true;
        }

        foreach (string earlier in EarlierRegistrationMembers)
        {
            changed |= registration.Remove(earlier);
        }

        return changed;
    }

    /// <summary>A fork base of a name and a store-given version, as the base it resolves to on this node.</summary>
    private static bool ExportForkBase(JsonObject drafted, Func<DefinitionKind, string, int, ForkBase?> currentBase)
    {
        if (drafted["forkBase"] is not JsonObject forkBase
            || forkBase["contentHash"] is JsonValue
            || forkBase["version"] is not JsonValue version
            || forkBase["name"] is not JsonValue name)
        {
            return false;
        }

        string definition = drafted["name"]?.GetValue<string>() ?? "(unnamed)";
        string scope = drafted["scope"]?.GetValue<string>() ?? "(no scope)";
        DefinitionKind kind = KindOf(drafted["kind"])
            ?? throw new DomainException($"The definition '{definition}' ({scope} scope) records a kind this build does not know, '{drafted["kind"]}', so its fork base cannot be resolved and nothing was exported.");
        string baseName = name.GetValue<string>();
        int baseVersion = version.GetValue<int>();
        ForkBase resolved = currentBase(kind, baseName, baseVersion)
            ?? throw new DomainException(
                $"The {kind} '{definition}' ({scope} scope{(drafted["project"]?.GetValue<string>() is { } project ? $", project '{project}'" : string.Empty)}, "
                + $"definition {drafted["definitionId"]?.GetValue<string>()}) was forked from '{baseName}' version {baseVersion}, "
                + "which resolves to no module-scope definition on this node, so nothing was exported.");
        drafted["forkBase"] = new JsonObject
        {
            ["name"] = resolved.Name,
            ["module"] = resolved.Module,
            ["moduleVersion"] = resolved.ModuleVersion,
            ["contentHash"] = resolved.ContentHash,
        };
        return true;
    }

    private static bool UpgradeDefinition(JsonObject definition)
    {
        if (definition["scope"] is not JsonValue scope || scope.GetValueKind() != JsonValueKind.String || scope.GetValue<string>() != ShippedScope)
        {
            return false;
        }

        definition["scope"] = nameof(DefinitionScope.Module);
        definition["module"] ??= SoftwareModule;
        definition["moduleVersion"] ??= SoftwareModuleVersion;
        return true;
    }

    /// <summary>
    /// Prefixes the performer named by <paramref name="performer"/> when it is one of the five unprefixed
    /// steps and the kind named by <paramref name="kind"/>, if any, is a system step.
    /// </summary>
    private static bool UpgradePerformer(JsonObject data, string? kind, string performer)
    {
        if (kind is not null && !IsSystemStep(data[kind]))
        {
            return false;
        }

        if (data[performer] is not JsonValue named || named.GetValueKind() != JsonValueKind.String)
        {
            return false;
        }

        string name = named.GetValue<string>();
        string current = CurrentStepName(name);
        if (current == name)
        {
            return false;
        }

        data[performer] = current;
        return true;
    }

    /// <summary>A definition kind stored as its name or as its number, or null for any other value.</summary>
    private static DefinitionKind? KindOf(JsonNode? kind) =>
        kind is JsonValue value
            ? value.GetValueKind() switch
            {
                JsonValueKind.String when Enum.TryParse(value.GetValue<string>(), out DefinitionKind named) && Enum.IsDefined(named) => named,
                JsonValueKind.Number when Enum.IsDefined((DefinitionKind)value.GetValue<int>()) => (DefinitionKind)value.GetValue<int>(),
                _ => null,
            }
            : null;

    private static bool IsText(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String;

    /// <summary>A step kind stored as its number or as its name.</summary>
    private static bool IsSystemStep(JsonNode? kind) =>
        kind is JsonValue value && value.GetValueKind() switch
        {
            JsonValueKind.Number => value.GetValue<int>() == (int)StepKind.SystemStep,
            JsonValueKind.String => value.GetValue<string>() == nameof(StepKind.SystemStep),
            _ => false,
        };

    private static bool RoutesOn(FlowStep step, string verdict) =>
        step.Transitions.Any(t =>
            t.Field == SystemStepAnswer.Verdict
            && t.Value.ValueKind == JsonValueKind.String
            && t.Value.GetString() == verdict);

    /// <summary>
    /// Reads a stored <typeparamref name="T"/> document after <paramref name="upgrade"/> has put its JSON in
    /// the current shape, and writes one as the store's serializer would without it.
    /// </summary>
    private sealed class UpgradingConverter<T>(Func<JsonObject, bool> upgrade) : JsonConverter<T>
    {
        private JsonSerializerOptions? inner;

        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            JsonNode? node = JsonNode.Parse(ref reader);
            if (node is JsonObject document)
            {
                upgrade(document);
            }

            return node is null
                ? default
                : node.Deserialize<T>(Inner(options));
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, Inner(options));

        /// <summary>The store's options without this converter, so the document itself is read and written as it always was.</summary>
        private JsonSerializerOptions Inner(JsonSerializerOptions options)
        {
            if (inner is { } held)
            {
                return held;
            }

            var without = new JsonSerializerOptions(options);
            without.Converters.Remove(this);
            return inner = without;
        }
    }
}

/// <summary>One event as the events table holds it: its stored type name and its JSON data.</summary>
public sealed record StoredEvent(string Type, string Data);
