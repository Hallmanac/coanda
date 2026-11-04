using Coanda.Core;
using Microsoft.Data.Sqlite;

namespace Coanda.Daemon.Store;

/// <summary>
/// Recognizes a node home that a build from before the 2026-11-03 vocabulary change (contour, flow,
/// pause) wrote. Nothing carries that record over: its event names, its documents, and the hashed
/// content of its types and contours all use the retired words, so this build would misread it or
/// fail on it part way through. The daemon refuses such a home before touching it, and says how to
/// start fresh.
/// </summary>
public static class PreRenameStore
{
    /// <summary>
    /// Every daemon seeds the shipped definitions on its first start, so every record holds them.
    /// A record from before the change shows it in two places: its contours were drafted under the
    /// kind <c>Flow</c>, and its parcel types have no <c>PausedState</c>, which every type this
    /// build reads requires.
    /// </summary>
    private const string Query =
        """
        SELECT EXISTS (
            SELECT 1 FROM fi_events
            WHERE type = 'definition_drafted'
              AND (json_extract(data, '$.kind') = 'Flow'
                   OR (json_extract(data, '$.kind') = 'ParcelType'
                       AND json_extract(json_extract(data, '$.content'), '$.PausedState') IS NULL)))
        """;

    /// <summary>
    /// Throws when the home's record was written before the change. A home with no record yet, or
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

        if (WrittenBeforeRename(home.DatabasePath))
        {
            throw new DomainException(
                $"The record at {home.DatabasePath} was written by a build from before contours, flows, and pauses "
                + "had those names, and this build cannot read it. Move the node home aside (for example "
                + $"'mv {home.Path} {home.Path}.pre-rename') and start again; a fresh home is created on the first start.");
        }
    }

    private static bool WrittenBeforeRename(string databasePath)
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
        command.CommandText = Query;
        return command.ExecuteScalar() is 1L;
    }
}
