using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Daemon.SystemSteps;
using Microsoft.Data.Sqlite;

namespace Coanda.Daemon.Store;

/// <summary>
/// Recognizes an event in a shape an earlier build wrote and this build does not read. Builds before
/// phase 3's gate wrote these shapes, and this build reads none of them: a node holding one is rebuilt
/// from an export, written by a build that still reads them, before this build starts on it. The daemon
/// refuses such a home (<see cref="ThrowIfFound"/>), as <see cref="PreRenameStore"/> refuses a home
/// written before the rename, and the ledger import refuses a peer's line in such a shape (<see cref="Of"/>),
/// since once stored it would read as something it is not.
/// <para>The shapes:</para>
/// <list type="number">
/// <item>A <c>definition_drafted</c> in the scope stored as <c>Shipped</c>, from before module scope.</item>
/// <item>A <c>flow_run_started</c>, <c>step_started</c>, <c>system_step_dispatched</c>, or
/// <c>system_step_completed</c> naming one of the five software steps as a system step without the
/// <c>software/</c> prefix, whether the step kind is stored as a number or a name.</item>
/// <item>A <c>project_registered</c> carrying a member of the registration from before repos (the remote,
/// the primary branch, or the build or test command), whether or not it also carries repos.</item>
/// <item>Any <c>project_required_checks_set</c>, the event that set the merge's checks before module settings.</item>
/// <item>A <c>definition_drafted</c> whose fork base has no content hash: a name and a store-given version,
/// from before fork bases named a content hash.</item>
/// </list>
/// <para>
/// None of these is an earlier shape: a flow whose content names a software step without the prefix,
/// since a definition's content and content hash are never changed (<see cref="UnprefixedSoftwareSteps"/>);
/// agent content that names one of the answer schemas build, review, fix, or discovery and holds no
/// schema content, for the same reason, which reads and hashes as it was stored and whose answers the
/// daemon validates against the document the release shipped under that name
/// (<see cref="Coanda.Core.Agents.NameOnlyAnswerSchemas"/>); and an <c>agent_step_spawned</c> with a
/// null agent hash, model, or launch id, since those members are nullable in the core for the records
/// written before they existed.
/// </para>
/// <para>
/// A document is a snapshot of a stream, or is rebuilt from one, so a stream holding an earlier event is
/// what marks a home; checking the events is enough.
/// </para>
/// </summary>
public static class EarlierShapes
{
    /// <summary>
    /// The commit on main at which the export and rebuild commands exist and every earlier shape still
    /// reads: the last before the change that deleted the reading of earlier shapes.
    /// </summary>
    public const string RebuildCommit = "ee75e50cea5bacb69c9326da51e88836573d9e9d";

    /// <summary>The page under <c>docs/</c> that gives the steps to rebuild a node's record.</summary>
    public const string RebuildSteps = "docs/rebuilding-the-record.md";

    private const string DefinitionDrafted = "definition_drafted";
    private const string ProjectRegistered = "project_registered";
    private const string ProjectRequiredChecksSet = "project_required_checks_set";
    private const string FlowRunStarted = "flow_run_started";
    private const string StepStarted = "step_started";
    private const string SystemStepDispatched = "system_step_dispatched";
    private const string SystemStepCompleted = "system_step_completed";
    private const string ShippedScope = "Shipped";

    /// <summary>The event types an earlier shape can be stored under.</summary>
    private static readonly IReadOnlyList<string> Types =
        [DefinitionDrafted, ProjectRegistered, ProjectRequiredChecksSet, FlowRunStarted, StepStarted, SystemStepDispatched, SystemStepCompleted];

    /// <summary>What a registration from before repos held beside the repos this build records.</summary>
    private static readonly IReadOnlyList<string> EarlierRegistrationMembers = ["remote", "primaryBranch", "buildCommand", "testCommand"];

    /// <summary>
    /// Why <paramref name="stored"/>, given as the events table holds it, is in an earlier shape, or null
    /// when it is not. It reads the event's JSON only, and changes nothing. Data that is not JSON, or a step
    /// kind that is a number no step kind is, throws.
    /// </summary>
    public static string? Of(StoredEvent stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (stored.Type == ProjectRequiredChecksSet)
        {
            return $"it is a '{ProjectRequiredChecksSet}', which set the merge's checks before module settings";
        }

        if (!Types.Contains(stored.Type, StringComparer.Ordinal) || JsonNode.Parse(stored.Data) is not JsonObject data)
        {
            return null;
        }

        return stored.Type switch
        {
            DefinitionDrafted when IsText(data["scope"], ShippedScope) =>
                $"its '{DefinitionDrafted}' is in the scope stored as '{ShippedScope}', from before module scope",
            DefinitionDrafted when data["forkBase"] is JsonObject forkBase && !IsText(forkBase["contentHash"]) =>
                $"its '{DefinitionDrafted}' has a fork base of a name and a store-given version, from before fork bases named a content hash",
            ProjectRegistered when EarlierRegistrationMembers.FirstOrDefault(data.ContainsKey) is { } member =>
                $"its '{ProjectRegistered}' carries '{member}', a member of the registration from before repos",
            FlowRunStarted => Unprefixed(data, stored.Type, "firstStepKind", "firstStepPerformer"),
            StepStarted => Unprefixed(data, stored.Type, "kind", "performer"),
            SystemStepDispatched or SystemStepCompleted => Unprefixed(data, stored.Type, kind: null, "systemStep"),
            _ => null,
        };
    }

    /// <summary>
    /// Throws when the home's record holds an event in an earlier shape, saying how to rebuild it. A home
    /// with no record yet, or one with no events table, passes. The record is opened read-only and without
    /// creating a companion file, so a refused home's record is left exactly as it was.
    /// </summary>
    public static void ThrowIfFound(NodeHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        if (!File.Exists(home.DatabasePath))
        {
            return;
        }

        if (FirstIn(home.DatabasePath) is { } why)
        {
            throw new DomainException(Refusal(home, why));
        }
    }

    /// <summary>The refusal of a home whose record holds an earlier shape, the same whichever command refuses it.</summary>
    public static string Refusal(NodeHome home, string why)
    {
        ArgumentNullException.ThrowIfNull(home);
        return $"The record at {home.DatabasePath} holds an event in a shape an earlier build wrote, which this build does not read ({why}). "
            + $"Rebuild the record with the build at commit {RebuildCommit} on main, which still reads every earlier shape and holds the "
            + "export and rebuild commands: stop the daemon, run that build's 'coa record export', then its 'coa record rebuild <export>', "
            + $"as {RebuildSteps} describes, and start this build again. The record was left as it was.";
    }

    /// <summary>Why the first earlier-shape event the record holds is earlier, or null when it holds none.</summary>
    private static string? FirstIn(string databasePath)
    {
        using SqliteConnection connection = RecordExport.ConnectUnchanged(databasePath);
        using SqliteCommand table = connection.CreateCommand();
        table.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fi_events')";
        if (table.ExecuteScalar() is not 1L)
        {
            return null;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT type, data FROM fi_events WHERE type IN ({string.Join(", ", Types.Select((_, i) => $"$type{i}"))}) ORDER BY seq_id";
        for (int i = 0; i < Types.Count; i++)
        {
            command.Parameters.AddWithValue($"$type{i}", Types[i]);
        }

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Of(new StoredEvent(reader.GetString(0), reader.GetString(1))) is { } why)
            {
                return why;
            }
        }

        return null;
    }

    /// <summary>
    /// Why an event naming its performer in <paramref name="performer"/> names a software step without the
    /// prefix, when the kind named by <paramref name="kind"/>, if any, is a system step; or null.
    /// </summary>
    private static string? Unprefixed(JsonObject data, string type, string? kind, string performer) =>
        (kind is null || IsSystemStep(data[kind]))
        && data[performer] is JsonValue named
        && named.GetValueKind() == JsonValueKind.String
        && UnprefixedSoftwareSteps.Names.Contains(named.GetValue<string>(), StringComparer.Ordinal)
            ? $"its '{type}' names the system step '{named.GetValue<string>()}' without the '{UnprefixedSoftwareSteps.Module}/' prefix"
            : null;

    private static bool IsText(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String;

    private static bool IsText(JsonNode? node, string text) => IsText(node) && node?.GetValue<string>() == text;

    /// <summary>A step kind stored as its number or as its name; a number that is no integer throws.</summary>
    private static bool IsSystemStep(JsonNode? kind) =>
        kind is JsonValue value && value.GetValueKind() switch
        {
            JsonValueKind.Number => value.GetValue<int>() == (int)StepKind.SystemStep,
            JsonValueKind.String => value.GetValue<string>() == nameof(StepKind.SystemStep),
            _ => false,
        };
}
