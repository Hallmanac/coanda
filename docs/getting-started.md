# Getting started

Coanda is under construction. Phase 0 ships a binary, `coa`, that starts a daemon on your machine
and keeps its record in one SQLite file in your home directory.

```
coa project add try-coanda --repo git@github.com:you/try-coanda.git --branch main
coa parcel add --project try-coanda --type task --title "Try Coanda"
coa parcel list --project try-coanda
```

A project records the one repository its work acts on, its remote and its primary branch, so a
parcel can be created only in a project that `coa project add` has registered. The remote is
stored as given, and nothing contacts it until a build flow needs a worktree. Each build flow then
works in a git worktree of the node's own clone of that remote, under the node home, on a branch
named `coanda/<short parcel id>`, and its gates run the project's build and test commands there:
`dotnet build` and `dotnet test` unless `coa project add` was given `--build <command>` or
`--test <command>`, and `coa project show` prints the two the gates will run. Each of the two may
run for an hour; one still running then is ended, along with the processes it started that are
still its descendants, and the gates step fails as it would for a nonzero exit, so the work goes to
its fix lap. A process that detached from the command's process tree (an MSBuild worker node or the
VBCSCompiler server that `dotnet build` leaves behind, for instance) is not ended and may go on
running in the worktree. A project name that differs from a registered one only in letter case is
refused, because the node keeps each project's clone and work in a directory named after it. The daemon keeps its own files under `.coanda/` at the
worktree's root, so a commit that tracks `.coanda` in any letter case gets no worktree, and the
flow pauses naming what it tracks there. The worktree is
removed when the flow ends or is stopped; the branch stays in the clone. Without `--project`, a command works in the project that
`COANDA_PROJECT` names, then in the one named after the current directory.

So `coa daemon run` (and the daemon `coa` starts for you) needs a few things before a build flow
reaches its worktree step, and a few more, described in the next paragraph, before it can finish: a
github.com remote and a signed-in GitHub CLI on the daemon's PATH. Git 2.31 or later has to be on
the daemon's PATH, along with whatever the project's build and test commands run (the .NET SDK for
the defaults). The daemon also has to reach the remote without anyone answering a prompt, because it
runs every git command with `GIT_TERMINAL_PROMPT=0` and `GIT_SSH_COMMAND=ssh -o BatchMode=yes`. For
an ssh remote, that means a key without a passphrase or one loaded in an ssh agent the daemon can
reach. `GIT_SSH_COMMAND` also overrides any `core.sshCommand` or `GIT_SSH` you set, so a key named
only there is not used; name it in `~/.ssh/config` instead, which still applies. For an https
remote, a credential helper that already holds the credential works. When any of these is missing,
the flow pauses at its worktree step and the pause quotes git's own error.

A build flow ends once its pull request is open. Its open-pr step pushes the flow's branch to the
remote with git's own credentials, opens a pull request from it against the primary branch (or
reuses the open one that already has that branch as its head), records it in the task's pull-request
field, and moves the task to review; the merge is a contour of its own. So the remote has to be a
github.com repository, written as `https://github.com/<owner>/<repo>`,
`git@github.com:<owner>/<repo>`, or `ssh://git@github.com/<owner>/<repo>` (with or without `.git`),
and the GitHub CLI has to be on the daemon's PATH and signed in, because the daemon calls GitHub's
API with the token `gh auth token --hostname github.com` prints. Running `gh auth login` and then
`gh auth setup-git` covers both the token and the push. The worktree step fetches the flow's branch
from the remote before it checks it out, so a branch someone updated there since the node last
pushed it (a reviewer's "Update branch" or committed suggestion) is built on, and a branch deleted
there is pushed again. When the node's branch and the remote's each have commits the other lacks,
the worktree step pauses and names how to reconcile them by hand in the node's clone. The push is
guarded by a lease on what the node last fetched or pushed, so a branch someone else updates on the
remote while a flow builds is never overwritten: the flow pauses instead, and the way out is to stop
it and start a new flow on the parcel, whose worktree step fetches the remote's commits. Every other
failure (changes left in the worktree, a remote in another form, no token, a push or a request
GitHub refuses) pauses the flow at its open-pr step with the reason, and a resume performs the step
again.

When the build flow ends, the merge contour follows it on the same task with no step taken by hand.
It inspects the pull request and merges it through GitHub's API, never through auto-merge, and only
when the pull request is open against the primary branch, its head is the commit the build pushed,
that head is not behind the primary branch, GitHub reports it mergeable, and every check the
project names has succeeded with no other check failed or still running. The merge then closes the
task as completed. While checks are still running it waits a minute and looks again, for twenty
minutes in all, and then pauses. A resume after that pause looks once more and pauses again if a
check is still running, so resume it once the checks have finished. A project names no checks until you set them, and a merge pauses
until it does, because without names it cannot tell whether every check has reported yet. Name the
checks by the names GitHub shows for them; for coanda's own repository those are its two CI legs:

```bash
coa project set coanda --required-check "build-test (ubuntu-latest)" --required-check "build-test (windows-latest)"
```

The checks given replace the project's list, and `coa project show` prints them. When the branch is
behind the primary branch the merge pauses rather than land a tree CI never ran. The way out is to
stop the merge flow, bring the primary branch into `coanda/<short id>` with a merge, and move the
task back to build, so the build contour gates, reviews, and pushes the updated branch and a merge
follows again. The merge can be GitHub's "Update branch" with a merge commit on the pull request,
which the worktree step fetches and builds on, or a merge in the node's clone that you do not push,
which the build pushes. Do not rebase: a rebased branch no longer contains the commits the
remote's branch holds, and the worktree step refuses it. The merge compares the head with the
primary branch again right before it merges, but GitHub's merge takes no expected base, so a commit
that lands on the primary branch in the moment between that comparison and the merge is still
merged onto.

The lifecycle needs no configuring. The software engineering lifecycle is installed by default.

Judgments run on Claude Code. With no configuration at all, the daemon runs each judgment as a
headless session of the `claude` executable it finds on the PATH, under whatever login that
executable already has, so install Claude Code and sign in to it before a flow reaches its first
judgment. What a node runs its judgments with is configured once per node, in `node.json` in the
node home: the executor (`claude-code`, with an optional path to the `claude` executable), and the
credential profiles a project can name with `coa project set-profile`.
[Node configuration](node-configuration.md) describes the file. A judgment's session can run `coa` itself, as a
principal bound to its own flow; [how a session talks back](node-configuration.md#how-a-session-talks-back)
names the variables it is given. The repository's Rider run
configurations use `.coanda-dev` as their node home, and its checked-in `node.json` names the
`scripted` executor, so a judgment run from them pauses at no cost instead of starting a Claude Code
session; change that file to `claude-code` to run real sessions there. The worktree, gates, and
open-pr steps are real even there: a build flow fetches from the project's remote, runs its build and
test commands, and pushes and opens its pull request on GitHub, so the prerequisites above apply to
that home too. A flow run there pushes a real `coanda/<id>` branch and opens a real pull request on
the project's remote, and once the checks the project names pass, the merge that follows merges it,
so register a scratch repository in that home rather than one whose pull requests other people watch.

If you ran a build from before 2026-11-03, its record does not carry over, and the daemon says so
when it starts. Move `~/.coanda` aside and run the commands above again. The commands that drive
flows also changed: `coa contour start <contour> --parcel <id>` starts a flow, and `coa flow list`
and `coa flow show` read them. [The overview](overview.md#operating) has the full list.
