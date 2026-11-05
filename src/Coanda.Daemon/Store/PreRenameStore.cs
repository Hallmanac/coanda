using Coanda.Core;
using Microsoft.Data.Sqlite;

namespace Coanda.Daemon.Store;

/// <summary>
/// Recognizes a node home that a build from before one of the vocabulary changes wrote. The
/// 2026-11-03 change (contour, flow, pause) renamed the event names, the documents, and the hashed
/// content of types and contours. The 2026-11-04 changes renamed one execution of a contour on a
/// parcel from flow to flow run, and with it the events that execution records; the definition a flow
/// run follows from contour to flow, and with it the definition kind; and a position in a type's
/// lifecycle from state to stage and the step kinds from judgment and action to agent step and system
/// step, and with them the hashed content of types and flows. Nothing carries any of these records
/// over, so this build would misread it or fail on it part way through. The daemon refuses such a
/// home before touching it, and says how to start fresh.
/// </summary>
public static class PreRenameStore
{
    /// <summary>
    /// A record this build wrote holds parcel types once it holds anything at all: on a fresh home
    /// nothing records an event before the daemon records the parcel types its modules bring, and
    /// every type this build writes carries a <c>PausedStage</c>. A record written before the stage
    /// name holds types with none: those written since 2026-11-03 carry a <c>PausedState</c> in its
    /// place, and older ones carry neither. A record written before the daemon recorded definitions
    /// at all holds events and no parcel types, and its <c>parcel_created</c> events carry a state
    /// rather than a stage. That covers every record the earlier changes left too, since each of
    /// those was written before this one. A record with no events, such as one from a first start
    /// that failed before the import, passes.
    /// </summary>
    private const string Query =
        """
        SELECT EXISTS (
            SELECT 1 FROM fi_events
            WHERE type = 'definition_drafted'
              AND json_extract(data, '$.kind') = 'ParcelType'
              AND json_extract(json_extract(data, '$.content'), '$.PausedStage') IS NULL)
        OR (EXISTS (SELECT 1 FROM fi_events)
            AND NOT EXISTS (
                SELECT 1 FROM fi_events
                WHERE type = 'definition_drafted'
                  AND json_extract(data, '$.kind') = 'ParcelType'))
        """;

    /// <summary>
    /// Throws when the home's record was written before the stage and step names, or before the
    /// daemon recorded definitions. A home with no record yet, an empty record, or one this build
    /// wrote, passes. The record is opened read-only, so a refused home is left exactly as it was.
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
                $"The record at {home.DatabasePath} was written by a build from before the stage and step names "
                + "(a position in a type's lifecycle was called a state then, and a step was a judgment or an action), "
                + "and this build cannot read it. Move the node home aside (for example "
                + $"'mv {home.Path} {home.Path}.pre-stage') and start again; a fresh home is created on the first start.");
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
