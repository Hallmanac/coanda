using System.Globalization;
using Coanda.Daemon.Projects;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Repositories;

namespace Coanda.Daemon.Transport;

/// <summary>
/// Writes a node's ledger in one project onto <c>refs/coanda/nodes/&lt;node-id&gt;</c> in a clone, and pushes
/// that ref alone to the clone's remote. The ref's tree is made from the segments every time it is written,
/// never from what the ref held, so a ref deleted or rewound in the clone is written again with the same
/// files; a ref whose tree already is the segments' is left as it is. The push sends the ref only when the
/// files the remote's ref holds are each a prefix, at a line's end, of the file of the same name here, so a
/// push never removes or changes a line the remote holds, and it is leased on the commit the remote was
/// seen at, so a ref rebuilt here, with a history the remote's does not share, still pushes over it.
/// </summary>
/// <remarks>
/// What was last written and pushed is kept per clone for the daemon's run alone, so an idle round asks git
/// one thing per clone and contacts no remote; a start writes and checks every clone once.
/// </remarks>
internal sealed class LedgerRefs(Git git, Guid nodeId)
{
    private const string FileMode = "100644";

    private readonly Dictionary<string, (string LastHash, string Commit)> written = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> pushed = new(StringComparer.Ordinal);

    /// <summary>The ref this node writes its ledger on.</summary>
    public string RefName { get; } = $"refs/coanda/nodes/{nodeId:D}";

    /// <summary>Where the clone's ledger refspec fetches the remote's copy of this node's ref.</summary>
    private string FetchedRefName => $"{RepoClones.LedgerNamespace}{nodeId:D}";

    /// <summary>Writes the segments onto the ref in <paramref name="clone"/> unless it already holds them, then pushes the ref unless the remote was already given it.</summary>
    public async Task WriteAndPushAsync(string clone, LedgerSegments segments, CancellationToken cancellationToken)
    {
        string? current = await CommitOfAsync(clone, RefName, cancellationToken);
        string commit = written.TryGetValue(clone, out (string LastHash, string Commit) last) && last.LastHash == segments.LastHash && last.Commit == current && current is not null
            ? current
            : await WriteAsync(clone, segments, current, cancellationToken);
        written[clone] = (segments.LastHash, commit);
        if (pushed.TryGetValue(clone, out string? sent) && sent == commit)
        {
            return;
        }

        await PushAsync(clone, commit, segments, cancellationToken);
        pushed[clone] = commit;
    }

    private async Task<string> WriteAsync(string clone, LedgerSegments segments, string? current, CancellationToken cancellationToken)
    {
        DirectoryInfo scratch = Directory.CreateTempSubdirectory("coanda-ledger-");
        try
        {
            string[] names = [.. segments.Files.Keys];
            string[] paths = [.. names.Select(name => Path.Combine(scratch.FullName, name))];
            for (int i = 0; i < names.Length; i++)
            {
                await File.WriteAllBytesAsync(paths[i], segments.Files[names[i]], cancellationToken);
            }

            string[] blobs = Lines(await git.RunAsync(Git.Against(clone, ["hash-object", "-w", "--no-filters", "--", .. paths]), cancellationToken));
            var index = new Dictionary<string, string>(Git.Environment, StringComparer.Ordinal)
            {
                ["GIT_INDEX_FILE"] = Path.Combine(scratch.FullName, "index"),
            };
            string[] entries = [.. names.SelectMany((name, i) => new[] { "--cacheinfo", $"{FileMode},{blobs[i]},{name}" })];
            await git.RunAsync(Git.Against(clone, ["update-index", "--add", .. entries]) with { Environment = index }, cancellationToken);
            string tree = (await git.RunAsync(Git.Against(clone, "write-tree") with { Environment = index }, cancellationToken)).Trim();
            if (current is not null && await CommitOfAsync(clone, $"{current}^{{tree}}", cancellationToken) == tree)
            {
                return current;
            }

            var author = new Dictionary<string, string>(Git.Environment, StringComparer.Ordinal)
            {
                ["GIT_AUTHOR_NAME"] = "coanda",
                ["GIT_AUTHOR_EMAIL"] = $"{nodeId:D}@nodes.coanda",
                ["GIT_COMMITTER_NAME"] = "coanda",
                ["GIT_COMMITTER_EMAIL"] = $"{nodeId:D}@nodes.coanda",
            };
            string[] parent = current is null ? [] : ["-p", current];
            string message = string.Create(CultureInfo.InvariantCulture, $"The ledger of node {nodeId:D} through line {segments.LastHash}");
            string commit = (await git.RunAsync(Git.Against(clone, ["commit-tree", tree, .. parent, "-m", message]) with { Environment = author }, cancellationToken)).Trim();
            string[] expected = current is null ? [] : [current];
            await git.RunAsync(Git.Against(clone, ["update-ref", RefName, commit, .. expected]), cancellationToken);
            return commit;
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Pushes <paramref name="commit"/> to the remote's ref, after checking that every file the remote's
    /// ref holds is a prefix of this node's file of that name; a remote holding anything else is refused.
    /// </summary>
    private async Task PushAsync(string clone, string commit, LedgerSegments segments, CancellationToken cancellationToken)
    {
        string? remote = Lines(await git.RunAsync(Git.Against(clone, "ls-remote", Git.Remote, RefName), cancellationToken))
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length == 2 && parts[1] == RefName)
            .Select(parts => parts[0])
            .FirstOrDefault();
        if (remote == commit)
        {
            return;
        }

        if (remote is not null)
        {
            await git.RunAsync(Git.Against(clone, "fetch", "--no-tags", Git.Remote, $"+{RefName}:{FetchedRefName}"), cancellationToken);
            await RequireHeldAsync(clone, remote, commit, segments, cancellationToken);
        }

        await git.RunAsync(
            Git.Against(clone, "push", "--no-verify", $"--force-with-lease={RefName}:{remote}", Git.Remote, $"{RefName}:{RefName}"),
            cancellationToken);
    }

    /// <summary>Throws unless each file of the remote's commit is, at a line's end, a prefix of this node's file of the same name.</summary>
    private async Task RequireHeldAsync(string clone, string remote, string commit, LedgerSegments segments, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> ours = await BlobsAsync(clone, commit, cancellationToken);
        foreach ((string name, string blob) in await BlobsAsync(clone, remote, cancellationToken))
        {
            if (!segments.Files.TryGetValue(name, out byte[]? bytes))
            {
                throw Refused(name, "which this node's ledger has no file of");
            }

            if (ours.GetValueOrDefault(name) == blob)
            {
                continue;
            }

            long size = long.Parse((await git.RunAsync(Git.Against(clone, "cat-file", "-s", blob), cancellationToken)).Trim(), CultureInfo.InvariantCulture);
            if (size > bytes.Length || (size > 0 && bytes[size - 1] != (byte)'\n') || await BlobOfAsync(clone, bytes.AsMemory(0, (int)size), cancellationToken) != blob)
            {
                throw Refused(name, "whose lines are not the first lines of this node's file of that name");
            }
        }
    }

    private InvalidOperationException Refused(string name, string why) =>
        new($"the remote's {RefName} holds '{name}', {why}, so nothing was pushed: the push would remove or change a line the remote holds");

    /// <summary>The id git gives a blob of <paramref name="bytes"/>, written nowhere.</summary>
    private async Task<string> BlobOfAsync(string clone, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"coanda-ledger-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(scratch, bytes, cancellationToken);
            return (await git.RunAsync(Git.Against(clone, "hash-object", "--no-filters", "--", scratch), cancellationToken)).Trim();
        }
        finally
        {
            File.Delete(scratch);
        }
    }

    /// <summary>The blob of each file at the top of <paramref name="commit"/>'s tree, by name.</summary>
    private async Task<IReadOnlyDictionary<string, string>> BlobsAsync(string clone, string commit, CancellationToken cancellationToken)
    {
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string entry in Lines(await git.RunAsync(Git.Against(clone, "ls-tree", "--full-tree", commit), cancellationToken)))
        {
            // <mode> SP <type> SP <object> TAB <name>
            string[] parts = entry.Split('\t', 2);
            string[] described = parts[0].Split(' ');
            blobs[parts[1]] = described is [_, "blob", { } blob]
                ? blob
                : throw Refused(parts[1], "which is not a file");
        }

        return blobs;
    }

    /// <summary>The commit (or the object) <paramref name="revision"/> names in the clone, or null when it names none.</summary>
    private async Task<string?> CommitOfAsync(string clone, string revision, CancellationToken cancellationToken)
    {
        CommandResult result = await git.TryRunAsync(Git.Against(clone, "rev-parse", "--verify", "--quiet", revision), cancellationToken);
        return result.Succeeded && result.StandardOutput.Trim() is { Length: > 0 } found ? found : null;
    }

    private static string[] Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
