using System.Text.RegularExpressions;
using Coanda.Daemon.Projects;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Repositories;

namespace Coanda.Daemon.Transport;

/// <summary>
/// The git side of the import: fetches every peer's ledger ref from a clone's remote into the namespace the
/// clone's configuration sets aside for them (<see cref="RepoClones.LedgerNamespace"/>), lists the refs there,
/// and reads a ref's segment files. The fetch names the ledger refspec and an exclusion of this node's own
/// ref and nothing else, with tag following off, so it moves no branch, tag, remote-tracking ref, or ref this
/// node writes itself, and writes no <c>FETCH_HEAD</c> a module's own fetch might be writing.
/// </summary>
internal sealed partial class PeerRefs(Git git, Guid nodeId)
{
    private const string FileMode = "100644";

    /// <summary>The fetch every import round runs in each clone.</summary>
    public CommandLine FetchCommand(string clone) =>
        Git.Against(clone, "fetch", "--no-tags", "--no-write-fetch-head", Git.Remote, RepoClones.LedgerRefspec, $"^refs/coanda/nodes/{nodeId:D}");

    public async Task FetchAsync(string clone, CancellationToken cancellationToken) =>
        await git.RunAsync(FetchCommand(clone), cancellationToken);

    /// <summary>
    /// Every fetched ledger ref in the clone named for another node, by that node's id, with the commit it
    /// names. A ref whose name under the namespace is not a node id, and the one named for this node, are
    /// left out.
    /// </summary>
    public async Task<IReadOnlyList<(Guid Origin, string Commit)>> ListAsync(string clone, CancellationToken cancellationToken)
    {
        string listed = await git.RunAsync(Git.Against(clone, "for-each-ref", "--format=%(objectname) %(refname)", RepoClones.LedgerNamespace), cancellationToken);
        List<(Guid, string)> refs = [];
        foreach (string entry in Lines(listed))
        {
            string[] parts = entry.Split(' ', 2);
            if (parts is [string commit, string name]
                && name.StartsWith(RepoClones.LedgerNamespace, StringComparison.Ordinal)
                && Guid.TryParseExact(name[RepoClones.LedgerNamespace.Length..], "D", out Guid origin)
                && string.Equals(origin.ToString("D"), name[RepoClones.LedgerNamespace.Length..], StringComparison.Ordinal)
                && origin != nodeId)
            {
                refs.Add((origin, commit));
            }
        }

        return refs;
    }

    /// <summary>
    /// The lines of the segment files at the top of <paramref name="commit"/>'s tree, the files in name order
    /// and each file's lines in order, each with the segment it is in. Only files named <c>yyyy-MM.jsonl</c>
    /// are read, and with <paramref name="fromSegment"/> only those of that segment and later ones. A line is
    /// the bytes up to its line feed; a file's last line may lack one.
    /// </summary>
    /// <remarks>
    /// The files are written out by git into a scratch directory, from an index holding them alone, with no
    /// line-ending conversion and no attributes, so their bytes are the blobs' exactly whatever their size;
    /// the command runner keeps only the first part of a command's output.
    /// </remarks>
    public async Task<IReadOnlyList<RefLine>> ReadAsync(string clone, string commit, string? fromSegment, CancellationToken cancellationToken)
    {
        List<(string Segment, string Name, string Blob)> files = [];
        foreach (string entry in Lines(await git.RunAsync(Git.Against(clone, "ls-tree", "--full-tree", commit), cancellationToken)))
        {
            // <mode> SP <type> SP <object> TAB <name>
            string[] parts = entry.Split('\t', 2);
            if (parts is [string described, string name]
                && described.Split(' ') is [_, "blob", string blob]
                && SegmentName().IsMatch(name)
                && (fromSegment is null || string.CompareOrdinal(name[..^LedgerSegments.Extension.Length], fromSegment) >= 0))
            {
                files.Add((name[..^LedgerSegments.Extension.Length], name, blob));
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        if (files.Count == 0)
        {
            return [];
        }

        DirectoryInfo scratch = Directory.CreateTempSubdirectory("coanda-import-");
        try
        {
            string tree = Path.Combine(scratch.FullName, "files");
            Directory.CreateDirectory(tree);
            var variables = new Dictionary<string, string>(Git.Environment, StringComparer.Ordinal)
            {
                ["GIT_INDEX_FILE"] = Path.Combine(scratch.FullName, "index"),
                ["GIT_ATTR_NOSYSTEM"] = "1",
            };
            string[] entries = [.. files.SelectMany(f => new[] { "--cacheinfo", $"{FileMode},{f.Blob},{f.Name}" })];
            await git.RunAsync(Git.Against(clone, ["update-index", "--add", .. entries]) with { Environment = variables }, cancellationToken);
            await git.RunAsync(
                Git.Against(
                    clone,
                    "-c", "core.autocrlf=false",
                    "-c", "core.eol=lf",
                    "-c", $"core.attributesFile={Path.Combine(scratch.FullName, "no-attributes")}",
                    $"--work-tree={tree}",
                    "checkout-index", "--all", "--force") with { Environment = variables },
                cancellationToken);

            List<RefLine> lines = [];
            foreach ((string segment, string name, _) in files)
            {
                byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(tree, name), cancellationToken);
                int start = 0;
                while (start < bytes.Length)
                {
                    int end = Array.IndexOf(bytes, (byte)'\n', start);
                    int stop = end < 0 ? bytes.Length : end;
                    lines.Add(new RefLine(bytes[start..stop], segment));
                    start = stop + 1;
                }
            }

            return lines;
        }
        finally
        {
            Writable(scratch);
            scratch.Delete(recursive: true);
        }
    }

    /// <summary>Clears the read-only mark git may leave on what it writes on Windows, so the scratch directory can be deleted.</summary>
    private static void Writable(DirectoryInfo directory)
    {
        foreach (FileInfo file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    private static string[] Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}\.jsonl$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentName();
}

/// <summary>One line of a peer's ref, its bytes without the line feed, and the segment whose file holds it.</summary>
internal sealed record RefLine(byte[] Bytes, string Segment);
