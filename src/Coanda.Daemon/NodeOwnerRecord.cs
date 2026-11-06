using System.Text.Json;
using Coanda.Core;

namespace Coanda.Daemon;

/// <summary>
/// The node-local record of the node's one owner id (<see cref="NodeOwner"/>): one owner-only file in the
/// home, <see cref="NodeHome.NodeOwnerPath"/>, a JSON object with the one member <c>ownerId</c>. It is not
/// in the store, so it is never exported, and the CLI reads it to name the owner in its principal header.
/// It is written whole to a file of its own and renamed into place, so a reader sees either no record or a
/// whole one.
/// </summary>
public static class NodeOwnerRecord
{
    /// <summary>The owner id <paramref name="home"/> records, or null when it holds no record.</summary>
    public static string? Read(NodeHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        string content;
        try
        {
            content = File.ReadAllText(home.NodeOwnerPath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            if (document.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.EnumerateObject().Count() == 1
                && root.TryGetProperty("ownerId", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { } ownerId
                && !string.IsNullOrWhiteSpace(ownerId))
            {
                return ownerId;
            }
        }
        catch (JsonException)
        {
        }

        throw new DomainException(
            $"The node's owner id record at {home.NodeOwnerPath} is not one JSON object holding one non-blank ownerId; restore it, or move it aside if this node has never registered or joined a project.");
    }

    /// <summary>Writes <paramref name="ownerId"/> as the owner id <paramref name="home"/> records, replacing none: a home with a record keeps it.</summary>
    public static void Write(NodeHome home, string ownerId)
    {
        ArgumentNullException.ThrowIfNull(home);
        OwnerOnly.EnsureDirectory(home.Path);
        string written = $"{home.NodeOwnerPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = OwnerOnly.Open(written, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("ownerId", ownerId);
                writer.WriteEndObject();
                writer.Flush();
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }

            File.Move(written, home.NodeOwnerPath, overwrite: false);
        }
        finally
        {
            File.Delete(written);
        }
    }
}
