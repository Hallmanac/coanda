using System.Text;
using Coanda.Core;
using Coanda.Daemon.Signing;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Fisher;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>
/// A peer's chain in one project, signed here rather than exported by a node, so a test can write any line,
/// sound or not, onto the peer's ref in a stand-in remote: <c>refs/coanda/nodes/&lt;origin&gt;</c>, one
/// segment file per month of the lines' recorded time.
/// </summary>
internal sealed class PeerChain(IDocumentStore store, Guid projectId, Guid origin, Ed25519Key key)
{
    /// <summary>When every line of a chain was recorded, unless a line says otherwise.</summary>
    public static readonly DateTimeOffset Recorded = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly List<byte[]> lines = [];

    public Guid Origin => origin;

    public Ed25519Key Key => key;

    public IReadOnlyList<byte[]> Lines => lines;

    public string RefName => $"refs/coanda/nodes/{origin:D}";

    /// <summary>A chain of a new origin with a key of its own.</summary>
    public static PeerChain Of(IDocumentStore store, Guid projectId, Guid? origin = null) =>
        new(store, projectId, origin ?? Ids.New(), Ed25519Key.Generate());

    /// <summary>A chain of the same origin and key holding this one's first <paramref name="keep"/> lines, to fork from there.</summary>
    public PeerChain Fork(int keep)
    {
        var fork = new PeerChain(store, projectId, origin, key);
        fork.lines.AddRange(lines.Take(keep));
        return fork;
    }

    /// <summary>The event as the events table holds it: its type name and its JSON.</summary>
    public StoredEvent Stored(object data) =>
        new(store.Options.EventGraph.EventMappingFor(data.GetType()).EventTypeName, store.Options.Serializer.ToJson(data));

    /// <summary>Appends <paramref name="data"/> as the chain's next line, on a stream of its own unless one is named.</summary>
    public byte[] Add(object data, Guid? stream = null, Ed25519Key? signer = null) =>
        Add(Stored(data), stream, signer);

    /// <summary>
    /// Appends <paramref name="stored"/> as the chain's next line: its sequence one past the last line's,
    /// naming that line's hash, on <paramref name="stream"/> or a stream of its own, signed by
    /// <paramref name="signer"/> or the chain's key, and naming <paramref name="lineOrigin"/> and
    /// <paramref name="lineProject"/> or the chain's own.
    /// </summary>
    public byte[] Add(StoredEvent stored, Guid? stream = null, Ed25519Key? signer = null, Guid? lineOrigin = null, Guid? lineProject = null)
    {
        string? previous = lines.Count == 0 ? null : SignedLine.HashOf(lines[^1]);
        var content = new LineContent(
            lineProject ?? projectId, lineOrigin ?? origin, lines.Count + 1, previous, LineTag.ProjectWide, stream ?? Ids.New(), Ids.New(), Recorded, stored);
        byte[] line = SignedLine.Sign(content, signer ?? key);
        lines.Add(line);
        return line;
    }

    /// <summary>
    /// Writes <paramref name="written"/>, the chain's lines unless others are given, as the whole of the
    /// origin's ref in the bare repository at <paramref name="remote"/>, replacing whatever it held, in one
    /// segment file: <paramref name="segment"/>'s, or the month of <see cref="Recorded"/>.
    /// </summary>
    public async Task PushAsync(string remote, IEnumerable<byte[]>? written = null, string? segment = null)
    {
        string text = string.Concat((written ?? lines).Select(l => Encoding.UTF8.GetString(l) + "\n"));
        DirectoryInfo scratch = Directory.CreateTempSubdirectory("coanda-peer-");
        try
        {
            string bytes = Path.Combine(scratch.FullName, "segment");
            await File.WriteAllBytesAsync(bytes, Encoding.UTF8.GetBytes(text));
            string blob = (await LedgerNode.GitAsync(remote, "hash-object", "-w", "--no-filters", "--", bytes)).Trim();
            var index = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = Path.Combine(scratch.FullName, "index") };
            string file = (segment ?? Recorded.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)) + LedgerSegments.Extension;
            await LedgerNode.GitAsync(remote, index, "update-index", "--add", "--cacheinfo", $"100644,{blob},{file}");
            string tree = (await LedgerNode.GitAsync(remote, index, "write-tree")).Trim();
            var author = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "peer",
                ["GIT_AUTHOR_EMAIL"] = "peer@nodes.coanda",
                ["GIT_COMMITTER_NAME"] = "peer",
                ["GIT_COMMITTER_EMAIL"] = "peer@nodes.coanda",
            };
            string commit = (await LedgerNode.GitAsync(remote, author, "commit-tree", tree, "-m", "The peer's ledger")).Trim();
            await LedgerNode.GitAsync(remote, "update-ref", RefName, commit);
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }
}
