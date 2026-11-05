using System.Text.Json;
using Coanda.Core;

namespace Coanda.Daemon.Store;

/// <summary>
/// A full export of a node's record, every event in this build's current shape, as
/// <c>coa record export</c> writes it and <c>coa record rebuild</c> reads it. The events are in the
/// record's order and numbered from one with no gap, and each keeps the id, stream, tenant, and time the
/// store recorded it with; an event left out shifts the sequence of every event after it, and the
/// version of every event after it in its stream, and each loop position among the documents is given
/// in the export's own sequences. <see cref="LastEvent"/> names the record's last event as the record
/// held it, by its id and its sequence there, whether or not the export holds it, so a rebuild can tell
/// the record has not moved since. The documents are every document table the record held other than the
/// inline snapshots, which a rebuild regenerates from the events.
/// </summary>
public sealed record RecordExportFile(
    int Format,
    string Build,
    DateTimeOffset ExportedAt,
    ExportedEventReference? LastEvent,
    IReadOnlyList<ExportedStream> Streams,
    IReadOnlyList<ExportedEvent> Events,
    IReadOnlyList<ExportedDocumentTable> Documents)
{
    /// <summary>The format this build writes and the only one it reads.</summary>
    public const int CurrentFormat = 1;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Reads the export at <paramref name="path"/>, or refuses, naming why, when it cannot be read as one this build writes.</summary>
    public static async Task<RecordExportFile> ReadAsync(string path, CancellationToken cancellationToken)
    {
        RecordExportFile? export;
        try
        {
            await using FileStream file = File.OpenRead(path);
            export = await JsonSerializer.DeserializeAsync<RecordExportFile>(file, Json, cancellationToken);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            throw new DomainException($"The export at {path} cannot be read ({failure.Message}), so nothing was changed.");
        }

        if (export is null || export.Streams is null || export.Events is null || export.Documents is null)
        {
            throw new DomainException($"The export at {path} is not a record export, so nothing was changed.");
        }

        if (export.Format != CurrentFormat)
        {
            throw new DomainException($"The export at {path} is in format {export.Format}, and this build reads format {CurrentFormat} only, so nothing was changed.");
        }

        for (int i = 0; i < export.Events.Count; i++)
        {
            if (export.Events[i] is not { } exported || exported.Sequence != i + 1 || exported.Data is null)
            {
                throw new DomainException($"The export at {path} does not number its events from one with no gap at its event {i + 1}, so nothing was changed.");
            }
        }

        return export;
    }
}

/// <summary>An event named by its id and the sequence the record held it at.</summary>
public sealed record ExportedEventReference(string Id, long Sequence);

/// <summary>One stream as the streams table holds it, with its version counting only the events the export holds.</summary>
public sealed record ExportedStream(string Tenant, string Id, string? Type, long Version, string Timestamp, string Created, bool Archived);

/// <summary>One event in its current shape, at its sequence in the export.</summary>
public sealed record ExportedEvent(
    long Sequence,
    string Id,
    string Tenant,
    string StreamId,
    long Version,
    string Type,
    string? DotnetType,
    string Timestamp,
    bool Archived,
    string Data);

/// <summary>One document table the record held other than an inline snapshot's: its statements, table first, and its rows.</summary>
public sealed record ExportedDocumentTable(string Table, IReadOnlyList<string> Schema, IReadOnlyList<ExportedDocument> Rows);

/// <summary>One document as its table holds it.</summary>
public sealed record ExportedDocument(string Tenant, string Id, string Data, string? DotnetType, string LastModified);
