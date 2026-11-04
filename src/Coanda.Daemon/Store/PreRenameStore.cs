using Coanda.Core;
using Microsoft.Data.Sqlite;

namespace Coanda.Daemon.Store;

/// <summary>
/// Recognizes a node home that a build from before one of the vocabulary changes wrote. The
/// 2026-11-03 change (contour, flow, pause) renamed the event names, the documents, and the hashed
/// content of types and contours. The first 2026-11-04 change renamed one execution of a contour on a
/// parcel from flow to flow run, and with it the events that execution records; the second renamed
/// the definition a flow run follows from contour to flow, and with it the definition kind and the
/// hashed content of types and flows. Nothing carries any of these records over, so this build would
/// misread it or fail on it part way through. The daemon refuses such a home before touching it, and
/// says how to start fresh.
/// </summary>
public static class PreRenameStore
{
    /// <summary>
    /// Every daemon seeds the shipped definitions on its first start, so every record holds them.
    /// A record from before the flow name shows it in one of two places: a record written since
    /// 2026-11-03 drafted its flows under the retired kind <c>Contour</c>, and one written before
    /// that has parcel types with no <c>PausedState</c>, which every type this build reads requires.
    /// </summary>
    private const string Query =
        """
        SELECT EXISTS (
            SELECT 1 FROM fi_events
            WHERE type = 'definition_drafted'
              AND (json_extract(data, '$.kind') = 'Contour'
                   OR (json_extract(data, '$.kind') = 'ParcelType'
                       AND json_extract(json_extract(data, '$.content'), '$.PausedState') IS NULL)))
        """;

    /// <summary>
    /// A record from before the flow run name holds an execution only under the retired event
    /// names, the snake-cased names of the events as they were called then. A record that never
    /// started one holds none of them, and nothing else it holds was renamed, so it passes.
    /// </summary>
    private const string RetiredFlowEventsQuery =
        """
        SELECT EXISTS (
            SELECT 1 FROM fi_events
            WHERE type IN ('flow_started', 'flow_paused', 'flow_resumed', 'flow_ended', 'flow_stopped',
                           'parcel_flow_attached', 'parcel_flow_released'))
        """;

    /// <summary>
    /// Throws when the home's record was written before any of the changes. A home with no record yet, or
    /// one this build wrote, passes. The record is opened read-only, so a refused home is left
    /// exactly as it was.
    /// </summary>
    public static void ThrowIfFound(NodeHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        if (!File.Exists(home.DatabasePath))
        {
            return;
        }

        if (Holds(home.DatabasePath, Query))
        {
            throw new DomainException(
                $"The record at {home.DatabasePath} was written by a build from before the flow name "
                + "(the definition a flow run follows was called a contour then), and this build cannot read it. Move the node home aside (for example "
                + $"'mv {home.Path} {home.Path}.pre-flow') and start again; a fresh home is created on the first start.");
        }

        if (Holds(home.DatabasePath, RetiredFlowEventsQuery))
        {
            throw new DomainException(
                $"The record at {home.DatabasePath} holds a flow run started by a build from before the flow run name, "
                + "and this build cannot read it. Move the node home aside (for example "
                + $"'mv {home.Path} {home.Path}.pre-flow-run') and start again; a fresh home is created on the first start.");
        }
    }

    private static bool Holds(string databasePath, string query)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        using SqliteCommand events = connection.CreateCommand();
        events.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fi_events')";
        if (events.ExecuteScalar() is not 1L)
        {
            return false;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = query;
        return command.ExecuteScalar() is 1L;
    }
}
