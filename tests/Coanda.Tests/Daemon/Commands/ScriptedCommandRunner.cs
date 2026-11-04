using System.Text;
using Coanda.Daemon.Commands;

namespace Coanda.Tests.Daemon.Commands;

/// <summary>
/// A command runner that records every command and runs nothing. A test can script the answer to
/// any command; every other command is answered by a small model of the git state the commands
/// would leave (the branches, remote-tracking refs, configuration, and worktrees of each clone), so
/// by default the worktree step takes its first-run path and answers done, the gates pass, a
/// worktree the step added is listed until a removal removes it, and the open-pr step reads
/// <see cref="GitHubToken"/>, finds the worktree clean, and pushes, which sets the remote's branch
/// and the remote-tracking ref the step updates itself. The remote holds no branch but the ones a
/// push or <see cref="SetRemoteBranch"/> gave it, so fetching any other branch but the primary fails
/// as git does for a missing remote ref, and one commit is an ancestor of another only when it is
/// the same commit or <see cref="AddAncestry"/> said so. Answering a clone command (<c>git init</c>)
/// with success creates the empty directory it names and nothing else. A command run in a modelled
/// worktree's directory reads and moves that worktree's head: a checkout detaches it at a commit or
/// puts it on a branch, and a rebase moves a detached head to <see cref="RebasedOnto"/> of the head
/// and the commit it is rebased onto, unless <see cref="ConflictOnRebase"/> said it stops on a
/// conflict, which leaves it in progress until it is aborted. The remote's branches are listed by
/// <c>ls-remote</c>.
/// </summary>
public sealed class ScriptedCommandRunner : ICommandRunner
{
    /// <summary>The commit the model's fetch of a primary branch brings.</summary>
    public const string PrimaryHead = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>The token the model's <c>gh auth token</c> prints.</summary>
    public const string GitHubToken = "gho_scriptedTokenNoRecordHolds0123456789";

    private const string ParcelBranchPrefix = "coanda/";

    private readonly Lock gate = new();
    private readonly List<CommandLine> commands = [];
    private readonly List<(Func<CommandLine, bool> When, Func<CommandLine, CancellationToken, Task<CommandResult>> Answer)> scripted = [];
    private readonly Dictionary<string, Clone> clones = new(StringComparer.Ordinal);
    private readonly HashSet<(string Ancestor, string Descendant)> ancestry = [];
    private string[] conflicting = [];

    /// <summary>Every command run, in order.</summary>
    public IReadOnlyList<CommandLine> Commands
    {
        get
        {
            lock (gate)
            {
                return [.. commands];
            }
        }
    }

    /// <summary>The git commands run, each as its arguments after any <c>--git-dir</c>, joined by spaces.</summary>
    public IReadOnlyList<string> GitCalls => [.. Commands.Where(c => c.Program == "git").Select(c => string.Join(' ', c.GitArguments()))];

    /// <summary>The command lines run through the platform's shell, in order.</summary>
    public IReadOnlyList<string> ShellLines => [.. Commands.Select(c => c.ShellLine()).OfType<string>()];

    /// <summary>The commit the model's rebase of <paramref name="head"/> onto <paramref name="onto"/> makes.</summary>
    public static string RebasedOnto(string head, string onto) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes($"{head} onto {onto}")));

    /// <summary>Makes every later rebase stop on a conflict that leaves <paramref name="paths"/> unmerged; none given lets rebases finish again.</summary>
    public void ConflictOnRebase(params string[] paths)
    {
        lock (gate)
        {
            conflicting = paths;
        }
    }

    /// <summary>The commit the modelled clone's remote holds for <paramref name="branch"/>, or null when it holds none.</summary>
    public string? RemoteBranch(string clone, string branch)
    {
        lock (gate)
        {
            return CloneAt(clone).RemoteBranches.GetValueOrDefault(branch);
        }
    }

    /// <summary>The branch the modelled worktree at <paramref name="path"/> has checked out, or null when its head is detached or it is not listed.</summary>
    public string? CheckedOut(string clone, string path)
    {
        lock (gate)
        {
            return CloneAt(clone).Worktrees.FirstOrDefault(w => w.Path == path)?.Branch;
        }
    }

    /// <summary>A success with <paramref name="output"/> on standard output.</summary>
    public static CommandResult Ok(string output = "") => new(0, output, string.Empty, output);

    /// <summary>A failure with <paramref name="error"/> on standard error.</summary>
    public static CommandResult Fails(int exitCode, string error) => new(exitCode, string.Empty, error, error);

    /// <summary>Answers every command <paramref name="when"/> matches with <paramref name="result"/>; the latest script that matches wins.</summary>
    public ScriptedCommandRunner On(Func<CommandLine, bool> when, CommandResult result) =>
        On(when, (_, _) => Task.FromResult(result));

    /// <summary>Answers every command <paramref name="when"/> matches through <paramref name="answer"/>, which is given the command's token.</summary>
    public ScriptedCommandRunner On(Func<CommandLine, bool> when, Func<CommandLine, CancellationToken, Task<CommandResult>> answer)
    {
        lock (gate)
        {
            scripted.Add((when, answer));
        }

        return this;
    }

    /// <summary>Gives the modelled clone at <paramref name="clone"/> a branch, as an earlier flow run would have left it.</summary>
    public void AddBranch(string clone, string branch, string head)
    {
        lock (gate)
        {
            CloneAt(clone).Branches[branch] = head;
        }
    }

    /// <summary>Sets a configuration value of the modelled clone at <paramref name="clone"/>.</summary>
    public void SetConfig(string clone, string key, string value)
    {
        lock (gate)
        {
            CloneAt(clone).Config[key] = value;
        }
    }

    /// <summary>Lists a worktree in the modelled clone at <paramref name="clone"/>, with any extra porcelain lines given.</summary>
    public void AddWorktree(string clone, string path, string branch, string head, params string[] extra)
    {
        lock (gate)
        {
            CloneAt(clone).Worktrees.Add(new Worktree(path, branch, head, extra));
        }
    }

    /// <summary>Sets a remote-tracking ref of the modelled clone, as an earlier push or fetch would have left it.</summary>
    public void SetTrackingRef(string clone, string reference, string head)
    {
        lock (gate)
        {
            CloneAt(clone).TrackingRefs[reference] = head;
        }
    }

    /// <summary>Gives the modelled clone's remote a branch, as an earlier push, or someone else, would have left it.</summary>
    public void SetRemoteBranch(string clone, string branch, string head)
    {
        lock (gate)
        {
            CloneAt(clone).RemoteBranches[branch] = head;
        }
    }

    /// <summary>Removes a branch from the modelled clone's remote, as someone deleting it there would.</summary>
    public void DeleteRemoteBranch(string clone, string branch)
    {
        lock (gate)
        {
            CloneAt(clone).RemoteBranches.Remove(branch);
        }
    }

    /// <summary>Records that <paramref name="ancestor"/> is an ancestor of <paramref name="descendant"/> in every modelled clone.</summary>
    public void AddAncestry(string ancestor, string descendant)
    {
        lock (gate)
        {
            ancestry.Add((ancestor, descendant));
        }
    }

    /// <summary>The commit a branch of the modelled clone holds, or null when it holds none.</summary>
    public string? Branch(string clone, string branch)
    {
        lock (gate)
        {
            return CloneAt(clone).Branches.GetValueOrDefault(branch);
        }
    }

    /// <summary>The commit a remote-tracking ref of the modelled clone holds, or null when it holds none.</summary>
    public string? TrackingRef(string clone, string reference)
    {
        lock (gate)
        {
            return CloneAt(clone).TrackingRefs.GetValueOrDefault(reference);
        }
    }

    /// <summary>The configuration value of the modelled clone, or null when it has none.</summary>
    public string? Config(string clone, string key)
    {
        lock (gate)
        {
            return CloneAt(clone).Config.GetValueOrDefault(key);
        }
    }

    /// <summary>The paths of the worktrees the modelled clone lists.</summary>
    public IReadOnlyList<string> Worktrees(string clone)
    {
        lock (gate)
        {
            return [.. CloneAt(clone).Worktrees.Select(w => w.Path)];
        }
    }

    public Task<CommandResult> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        Func<CommandLine, CancellationToken, Task<CommandResult>>? answer;
        lock (gate)
        {
            commands.Add(command);
            answer = scripted.LastOrDefault(s => s.When(command)).Answer;
        }

        return answer is null
            ? Task.FromResult(Model(command))
            : answer(command, cancellationToken);
    }

    private CommandResult Model(CommandLine command)
    {
        if (command is { Program: "gh", Arguments: ["auth", "token", ..] })
        {
            return Ok(GitHubToken + "\n");
        }

        if (command.Program != "git")
        {
            return Ok();
        }

        if (command.Arguments is ["init", .., string created])
        {
            Directory.CreateDirectory(created);
            return Ok();
        }

        string? gitDir = command.GitDirectory();
        string[] arguments = command.GitArguments();
        lock (gate)
        {
            if (gitDir is null)
            {
                return InWorktree(command.WorkingDirectory, arguments);
            }

            Clone clone = CloneAt(gitDir);
            switch (arguments)
            {
                case ["rev-parse", "--is-bare-repository"]:
                    return Ok("true\n");
                case ["rev-parse", "--verify", "--quiet", string named] when named.StartsWith("refs/heads/", StringComparison.Ordinal) && named.EndsWith("^{commit}", StringComparison.Ordinal):
                    return Ok(clone.Branches.GetValueOrDefault(named["refs/heads/".Length..^"^{commit}".Length], PrimaryHead) + "\n");
                case ["rev-parse", "--verify", ..]:
                    return Ok(PrimaryHead + "\n");
                case ["worktree", "list", "--porcelain"]:
                    return Ok(clone.Listing(gitDir));
                case ["worktree", "add", "--quiet", string path, string branch]:
                    clone.Worktrees.Add(new Worktree(path, branch, clone.Branches.GetValueOrDefault(branch, PrimaryHead), []));
                    return Ok();
                case ["ls-remote", "--refs", _, string listed] when listed.StartsWith("refs/heads/", StringComparison.Ordinal):
                    return Ok(clone.RemoteBranches.TryGetValue(listed["refs/heads/".Length..], out string? listedTip)
                        ? $"{listedTip}\t{listed}\n"
                        : string.Empty);
                case ["worktree", "remove", .., string removed]:
                    clone.Worktrees.RemoveAll(w => w.Path == removed);
                    return Ok();
                case ["worktree", "prune"]:
                    clone.Worktrees.RemoveAll(w => w.Extra.Any(l => l.StartsWith("prunable", StringComparison.Ordinal)) || w.Extra.Contains("locked initializing") && !Directory.Exists(w.Path));
                    return Ok();
                case ["for-each-ref", _, string reference] when reference.StartsWith("refs/heads/", StringComparison.Ordinal):
                    return Ok(clone.Branches.TryGetValue(reference["refs/heads/".Length..], out string? tip) ? tip + "\n" : string.Empty);
                case ["for-each-ref", _, string reference]:
                    return Ok(clone.TrackingRefs.TryGetValue(reference, out string? tracked)
                        ? tracked + "\n"
                        : string.Empty);
                case ["fetch", .., string refspec] when refspec.StartsWith($"+refs/heads/{ParcelBranchPrefix}", StringComparison.Ordinal):
                    string fetched = refspec["+refs/heads/".Length..refspec.IndexOf(':', StringComparison.Ordinal)];
                    if (!clone.RemoteBranches.TryGetValue(fetched, out string? remoteTip))
                    {
                        return Fails(128, $"fatal: couldn't find remote ref refs/heads/{fetched}\n");
                    }

                    clone.TrackingRefs[refspec[(refspec.IndexOf(':', StringComparison.Ordinal) + 1)..]] = remoteTip;
                    return Ok();
                case ["push", .., string refspec] when refspec.Contains(":refs/heads/", StringComparison.Ordinal):
                    string pushedTo = refspec[(refspec.IndexOf(":refs/heads/", StringComparison.Ordinal) + ":refs/heads/".Length)..];
                    clone.RemoteBranches[pushedTo] = refspec[..refspec.IndexOf(':', StringComparison.Ordinal)];
                    return Ok();
                case ["merge-base", "--is-ancestor", string older, string newer]:
                    return older == newer || ancestry.Contains((older, newer))
                        ? Ok()
                        : Fails(1, string.Empty);
                case ["update-ref", "-d", string reference]:
                    clone.TrackingRefs.Remove(reference);
                    return Ok();
                case ["update-ref", string reference, string head, _] when reference.StartsWith("refs/heads/", StringComparison.Ordinal):
                    clone.Branches[reference["refs/heads/".Length..]] = head;
                    return Ok();
                case ["update-ref", string reference, string head]:
                    clone.TrackingRefs[reference] = head;
                    return Ok();
                case ["config", "--get", "--default", _, string key]:
                    return Ok(clone.Config.TryGetValue(key, out string? held) ? held + "\n" : "\n");
                case ["config", string key, string value]:
                    clone.Config[key] = value;
                    return Ok();
                case ["branch", "--no-track", string branch, string head]:
                    clone.Branches[branch] = head;
                    return Ok();
                default:
                    return Ok();
            }
        }
    }

    /// <summary>A command run in a worktree's directory, read against the modelled worktree listed there; one in no listed worktree succeeds and does nothing.</summary>
    private CommandResult InWorktree(string directory, string[] arguments)
    {
        (Clone Clone, Worktree Worktree)? found = clones.Values
            .SelectMany(c => c.Worktrees.Select(w => ((Clone Clone, Worktree Worktree)?)(c, w)))
            .FirstOrDefault(pair => pair?.Worktree.Path == directory);
        if (found is not ({ } clone, { } worktree))
        {
            return Ok();
        }

        switch (arguments)
        {
            case ["rebase", "--abort"]:
                if (worktree.RebasingFrom is not { } from)
                {
                    return Fails(128, "fatal: no rebase in progress\n");
                }

                worktree.Head = from;
                worktree.RebasingFrom = null;
                return Ok();
            case ["-c", _, "rebase", "--quiet", string onto]:
                if (conflicting.Length > 0)
                {
                    worktree.RebasingFrom = worktree.Head;
                    return Fails(1, $"CONFLICT (content): Merge conflict in {conflicting[0]}\nerror: could not apply {worktree.Head[..7]}... change\n");
                }

                worktree.Head = RebasedOnto(worktree.Head, onto);
                return Ok();
            case ["diff", "--name-only", "--diff-filter=U", "-z"]:
                return Ok(worktree.RebasingFrom is null
                    ? string.Empty
                    : string.Join('\0', conflicting) + "\0");
            case ["rev-parse", "--verify", "--quiet", "REBASE_HEAD"]:
                return worktree.RebasingFrom is { } stopped
                    ? Ok(stopped + "\n")
                    : Fails(1, string.Empty);
            case ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"]:
                return Ok(worktree.Head + "\n");
            case ["checkout", "--quiet", "--detach", string commit]:
                worktree.Branch = null;
                worktree.Head = commit;
                return Ok();
            case ["checkout", "--quiet", string branch]:
                worktree.Branch = branch;
                worktree.Head = clone.Branches.GetValueOrDefault(branch, PrimaryHead);
                return Ok();
            default:
                return Ok();
        }
    }

    private Clone CloneAt(string gitDir)
    {
        string key = Path.GetFullPath(gitDir);
        if (!clones.TryGetValue(key, out Clone? clone))
        {
            clone = new Clone();
            clones[key] = clone;
        }

        return clone;
    }

    private sealed class Worktree(string path, string? branch, string head, string[] extra)
    {
        public string Path { get; } = path;

        /// <summary>The branch checked out, or null when the head is detached.</summary>
        public string? Branch { get; set; } = branch;

        public string Head { get; set; } = head;

        public string[] Extra { get; } = extra;

        /// <summary>The head a rebase in progress started from, or null when none is in progress.</summary>
        public string? RebasingFrom { get; set; }
    }

    private sealed class Clone
    {
        public Dictionary<string, string> Branches { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Config { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> TrackingRefs { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> RemoteBranches { get; } = new(StringComparer.Ordinal);

        public List<Worktree> Worktrees { get; } = [];

        public string Listing(string gitDir)
        {
            var listing = new StringBuilder($"worktree {gitDir}\nbare\n\n");
            foreach (Worktree worktree in Worktrees)
            {
                listing.Append($"worktree {worktree.Path}\nHEAD {worktree.Head}\n")
                    .Append(worktree.Branch is null ? "detached\n" : $"branch refs/heads/{worktree.Branch}\n");
                foreach (string line in worktree.Extra)
                {
                    listing.Append(line).Append('\n');
                }

                listing.Append('\n');
            }

            return listing.ToString();
        }
    }
}

/// <summary>Reads the commands the worktree and gates actions build, for matching and asserting.</summary>
public static class CommandLines
{
    /// <summary>The repository a git command names with <c>--git-dir</c>, or null when it names none.</summary>
    public static string? GitDirectory(this CommandLine command) =>
        command.Arguments is [string first, ..] && first.StartsWith("--git-dir=", StringComparison.Ordinal) ? first["--git-dir=".Length..] : null;

    /// <summary>A git command's arguments after any <c>--git-dir</c>.</summary>
    public static string[] GitArguments(this CommandLine command) =>
        command.GitDirectory() is null ? [.. command.Arguments] : [.. command.Arguments.Skip(1)];

    /// <summary>Whether the command is git with arguments starting <paramref name="subcommand"/> after any <c>--git-dir</c>.</summary>
    public static bool IsGit(this CommandLine command, params string[] subcommand) =>
        command.Program == "git" && command.GitArguments().Take(subcommand.Length).SequenceEqual(subcommand);

    /// <summary>The command line a shell command runs, or null when the command is not run through the shell.</summary>
    public static string? ShellLine(this CommandLine command) => command switch
    {
        { Program: "/bin/sh", Arguments: ["-c", string line] } => line,
        { Program: "cmd.exe", VerbatimArguments: { } verbatim } when verbatim.StartsWith("/d /s /c \"", StringComparison.Ordinal) => verbatim["/d /s /c \"".Length..^1],
        _ => null,
    };

    /// <summary>Whether the command runs <paramref name="line"/> through the shell.</summary>
    public static bool IsShell(this CommandLine command, string line) => command.ShellLine() == line;
}
