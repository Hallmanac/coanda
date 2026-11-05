using System.Text.Json;
using Coanda.Core;
using Coanda.Daemon.Signing;

namespace Coanda.Daemon;

/// <summary>
/// Who a node is: its node id, a UUIDv7, and its ed25519 key pair, which signs every line it exports.
/// Both are made once, at the daemon's first start on a home, and kept together in one owner-only
/// file in the home, <see cref="NodeHome.NodeIdentityPath"/>, which every later start reads.
/// </summary>
/// <remarks>
/// The file is one JSON object with two members: <c>nodeId</c>, the node id in its hyphenated form,
/// and <c>privateKey</c>, the key's 32-byte private key in standard base64 with padding. The public
/// key is derived from the private key and is not stored. A file that does not hold both, or that
/// does not parse, is refused and left exactly as it is: making a new identity over it would give the
/// node a second name and a second key, and every line the first one signed would stop verifying as
/// the node's.
/// </remarks>
public sealed class NodeIdentity
{
    /// <summary>How long a start waits for another start that is making the identity before it gives up.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs after a start finds no identity and before it takes the lock to make one, so a test can
    /// have another start make its identity first. Flows to the code the test calls, and no further.
    /// </summary>
    internal static readonly AsyncLocal<Action?> BeforeLock = new();

    /// <summary>
    /// Runs after an identity is written whole and before it is moved into place, so a test can look at
    /// the written file or interrupt the start there. Flows to the code the test calls, and no further.
    /// </summary>
    internal static readonly AsyncLocal<Action?> BeforeMove = new();

    private NodeIdentity(Guid nodeId, Ed25519Key key)
    {
        NodeId = nodeId;
        Key = key;
    }

    public Guid NodeId { get; }

    public Ed25519Key Key { get; }

    /// <summary>
    /// The home's node identity, made and written when the home has none. A start making one holds an
    /// exclusive lock on <see cref="NodeHome.NodeIdentityLockPath"/> while it does, and looks again for
    /// an identity once it holds it, so of two starts at once the second finds and uses the first's.
    /// The identity is written whole to a file of its own, owner-only from its creation, and renamed
    /// into place, so an interrupted start leaves either no identity or a whole one. The lock is the
    /// operating system's on an open file, so a start that dies releases it.
    /// </summary>
    public static async ValueTask<NodeIdentity> EnsureAsync(NodeHome home, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(home);
        if (Read(home) is { } existing)
        {
            return existing;
        }

        BeforeLock.Value?.Invoke();
        await using FileStream held = await LockAsync(home, cancellationToken);
        if (Read(home) is { } madeMeanwhile)
        {
            return madeMeanwhile;
        }

        // On Windows the file takes the home's access list, so the home is made owner-only first.
        OwnerOnly.EnsureDirectory(home.Path);
        var made = new NodeIdentity(Ids.New(), Ed25519Key.Generate());
        string written = $"{home.NodeIdentityPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = OwnerOnly.Open(written, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(made.ToJson(), cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            BeforeMove.Value?.Invoke();
            File.Move(written, home.NodeIdentityPath, overwrite: false);
        }
        finally
        {
            File.Delete(written);
        }

        return Read(home)
            ?? throw new InvalidOperationException($"The node identity written to {home.NodeIdentityPath} could not be read back.");
    }

    /// <summary>
    /// The lock file, opened with no sharing, which on Windows is the file's share mode and on macOS and
    /// Linux an exclusive advisory lock; another start holding it is waited for, up to <see cref="LockWait"/>.
    /// </summary>
    private static async Task<FileStream> LockAsync(NodeHome home, CancellationToken cancellationToken)
    {
        DateTimeOffset giveUp = DateTimeOffset.UtcNow + LockWait;
        while (true)
        {
            try
            {
                return OwnerOnly.Open(home.NodeIdentityLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < giveUp)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            }
        }
    }

    /// <summary>
    /// The home's node identity, or none when the home has no identity file. An identity file that
    /// does not parse, or that holds a node id with no key or a key with no node id, throws a
    /// <see cref="DomainException"/> naming the file, and the file is left as it is.
    /// </summary>
    public static NodeIdentity? Read(NodeHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        string path = home.NodeIdentityPath;
        byte[] content;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            content = buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        return Parse(content, out string? fault) ?? throw Refused(path, fault);
    }

    private static NodeIdentity? Parse(byte[] content, out string? fault)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            fault = "it does not parse as JSON";
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                fault = "it is not a JSON object";
                return null;
            }

            bool hasId = root.TryGetProperty("nodeId", out JsonElement id);
            bool hasKey = root.TryGetProperty("privateKey", out JsonElement key);
            Guid nodeId = hasId && id.ValueKind == JsonValueKind.String && Guid.TryParseExact(id.GetString(), "D", out Guid parsed)
                ? parsed
                : Guid.Empty;
            Ed25519Key? privateKey = hasKey && key.ValueKind == JsonValueKind.String
                ? Ed25519Key.FromPrivateKeyText(key.GetString() ?? "")
                : null;
            fault = (hasId, hasKey) switch
            {
                (false, false) => "it holds neither a node id nor a key",
                (true, false) => "it holds a node id but no key",
                (false, true) => "it holds a key but no node id",
                _ when root.EnumerateObject().Count() != 2 => "it holds members other than one nodeId and one privateKey",
                _ when nodeId.Version != 7 => "its node id is not a UUIDv7 in hyphenated form",
                _ when privateKey is null => $"its key is not {Ed25519Key.PrivateKeyLength} bytes in standard base64",
                _ => null,
            };

            return fault is null && privateKey is not null
                ? new NodeIdentity(nodeId, privateKey)
                : null;
        }
    }

    private byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("nodeId", NodeId.ToString("D"));
            writer.WriteString("privateKey", Key.PrivateKeyText);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static DomainException Refused(string path, string? fault) =>
        new($"The node identity at {path} is not whole: {fault}. The daemon will not start over it, and makes no new identity in its place, "
            + "since a second identity would leave every line this node signed unverifiable as its own. Restore the file from a backup, "
            + "or, if this node has never exported a line, move it aside and start again for a new identity.");
}
