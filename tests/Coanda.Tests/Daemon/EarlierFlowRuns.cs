using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Definitions;
using Coanda.Tests.Core;
using Fisher;
using Microsoft.Data.Sqlite;

namespace Coanda.Tests.Daemon;

/// <summary>
/// Flow runs as a build from before the software module's carve-out recorded them, written straight into
/// the store rather than through this build's flow run service, which records only current shapes, for a
/// test that this build refuses a home holding one; and the events table's rows as they are stored, for a
/// test to see what a write left.
/// </summary>
public static class EarlierFlowRuns
{
    /// <summary>The flow run events that carry a system step's name, by their stored type names.</summary>
    public static readonly IReadOnlySet<string> PerformerEvents =
        new HashSet<string>(["flow_run_started", "step_started", "system_step_dispatched", "system_step_completed"], StringComparer.Ordinal);

    /// <summary>
    /// What such a build leaves when its daemon stops while the worktree step of a flow run of
    /// <see cref="PreviousFlows.Build"/>, published in <paramref name="project"/>, is dispatched: the flow
    /// run's start and its dispatch name the step <c>worktree</c>, unprefixed, and the parcel names the flow
    /// run as its live one. Gives the flow run's id and the dispatch's.
    /// </summary>
    public static async Task<(Guid FlowRunId, Guid DispatchId)> DispatchedAsync(
        IDocumentStore store, string project, Guid parcelId, CancellationToken cancellationToken)
    {
        Definition published = await new DefinitionResolver(store).ResolveAsync(project, DefinitionKind.Flow, PreviousFlows.Build.Name, cancellationToken)
            ?? throw new InvalidOperationException("The earlier build flow is not published.");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid flowRunId = Ids.New();
        Guid dispatchId = Ids.New();
        var started = new FlowRunStarted(
            flowRunId, parcelId, PreviousFlows.Build.Name, published.VersionNumber, PreviousFlows.Build.Hash, Principal.LocalOwner.Id,
            "worktree", StepKind.SystemStep, "worktree", now);
        var dispatched = new SystemStepDispatched(flowRunId, "worktree", "worktree", dispatchId, now);

        await using IDocumentSession session = store.LightweightSession(project);
        session.Events.Append(parcelId, new ParcelFlowRunAttached(parcelId, flowRunId, now));
        session.Events.StartStream<FlowRun>(flowRunId, started, dispatched);
        await session.SaveChangesAsync(cancellationToken);
        return (flowRunId, dispatchId);
    }

    /// <summary>Every row of the events table, in order, as it is stored.</summary>
    public static IReadOnlyList<StoredRow> Rows(string databasePath) => Read(databasePath, null);

    /// <summary>The rows of one stream, in order, as they are stored.</summary>
    public static IReadOnlyList<StoredRow> Rows(string databasePath, Guid streamId) => Read(databasePath, streamId);

    /// <summary>The system step name a stored flow run event carries, or null for an event that carries none or an agent's.</summary>
    public static string? SystemStepOf(StoredRow row)
    {
        using JsonDocument document = JsonDocument.Parse(row.Data);
        JsonElement data = document.RootElement;
        (string? kind, string performer) = row.Type switch
        {
            "flow_run_started" => ("firstStepKind", "firstStepPerformer"),
            "step_started" => ("kind", "performer"),
            "system_step_dispatched" or "system_step_completed" => (null, "systemStep"),
            _ => (null, string.Empty),
        };

        if (performer.Length == 0 || (kind is not null && !IsSystemStep(data.GetProperty(kind))))
        {
            return null;
        }

        return data.GetProperty(performer).GetString();
    }

    private static bool IsSystemStep(JsonElement kind) => kind.ValueKind switch
    {
        JsonValueKind.Number => kind.GetInt32() == (int)StepKind.SystemStep,
        JsonValueKind.String => kind.GetString() == nameof(StepKind.SystemStep),
        _ => false,
    };

    private static List<StoredRow> Read(string databasePath, Guid? streamId)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = streamId is null
            ? "SELECT seq_id, stream_id, version, type, data, tenant_id, is_archived FROM fi_events ORDER BY seq_id"
            : "SELECT seq_id, stream_id, version, type, data, tenant_id, is_archived FROM fi_events WHERE stream_id = $stream ORDER BY seq_id";
        if (streamId is Guid id)
        {
            command.Parameters.AddWithValue("$stream", id.ToString());
        }

        List<StoredRow> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new StoredRow(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6)));
        }

        return rows;
    }
}

/// <summary>One row of the events table, as it is stored.</summary>
public sealed record StoredRow(long Sequence, string StreamId, long Version, string Type, string Data, string Tenant, long Archived);
