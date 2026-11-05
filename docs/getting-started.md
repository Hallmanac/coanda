# Getting started

Coanda is under construction. Phase 0 ships a binary, `coa`, that starts a daemon on your machine
and keeps its record in one SQLite file in your home directory.

```
coa project add try-coanda --repo git@github.com:you/try-coanda.git --branch main
coa parcel add --project try-coanda --type task --title "Try Coanda"
coa parcel list --project try-coanda
```

A project records its name and the repos its work acts on, so a parcel can be created only in a
project that `coa project add` has registered. Registering it records one repo, at the remote
`--repo` names, stored as given, and named after the remote's last path segment, lowercased and
without `.git` (the remote above is the repo `try-coanda`); `coa project show` and `coa project list`
print each repo's name and remote. Nothing contacts the remote until a build flow run needs a
worktree. The primary branch, the build and test commands, and the checks a merge waits for are not
the project's: they are settings of the software engineering module, which every project has
switched on. `coa project add` records `--branch` as the module's `primary-branch` setting for the
repo, and `--build <command>` and `--test <command>` as its `build` and `test` settings when they are
given, all in the one command, so the build flow runs with nothing else set. Each build flow run then
works in a git worktree of the node's own clone of the repo, under the node home, on a branch named
`coanda/<short parcel id>`, and its gates run the module's build and test commands there:
`dotnet build` and `dotnet test` unless they were set, and `coa project module show try-coanda software`
prints the two the gates will run. `coa project module set` changes a setting later:

```bash
coa project module set try-coanda software test "dotnet test --no-build"
```

A task records the repo it works in, in its `repos` field, which the software module sets to the
project's one repo the first time one of its steps acts on the task. Each of the two commands may
run for an hour; one still running then is ended, along with the processes it started that are
still its descendants, and the gates step fails as it would for a nonzero exit, so the work goes to
its fix lap. A process that detached from the command's process tree (an MSBuild worker node or the
VBCSCompiler server that `dotnet build` leaves behind, for instance) is not ended and may go on
running in the worktree. A project name that differs from a registered one only in letter case is
refused, because the node keeps each project's clone and work in a directory named after it. The daemon keeps its own files under `.coanda/` at the
worktree's root, so a commit that tracks `.coanda` in any letter case gets no worktree, and the
flow run pauses naming what it tracks there. The worktree is
removed when the flow run ends or is stopped; the branch stays in the clone. Without `--project`, a command works in the project that
`COANDA_PROJECT` names, then in the one named after the current directory.

So `coa daemon run` (and the daemon `coa` starts for you) needs a few things before a build flow run
reaches its worktree step, and a few more, described in the next paragraph, before it can finish: a
github.com remote and a signed-in GitHub CLI on the daemon's PATH. Git 2.31 or later has to be on
the daemon's PATH, along with whatever the project's build and test commands run (the .NET SDK for
the defaults). The daemon also has to reach the remote without anyone answering a prompt, because it
runs every git command with `GIT_TERMINAL_PROMPT=0` and `GIT_SSH_COMMAND=ssh -o BatchMode=yes`. For
an ssh remote, that means a key without a passphrase or one loaded in an ssh agent the daemon can
reach. `GIT_SSH_COMMAND` also overrides any `core.sshCommand` or `GIT_SSH` you set, so a key named
only there is not used; name it in `~/.ssh/config` instead, which still applies. For an https
remote, a credential helper that already holds the credential works. When any of these is missing,
the flow run pauses at its worktree step and the pause quotes git's own error.

A build flow run ends once its pull request is open. Its open-pr step pushes the flow run's branch to the
remote with git's own credentials, opens a pull request from it against the primary branch (or
reuses the open one that already has that branch as its head), records it in the task's pull-request
field, and moves the task to review; the merge is a flow of its own. So the remote has to be a
github.com repository, written as `https://github.com/<owner>/<repo>`,
`git@github.com:<owner>/<repo>`, or `ssh://git@github.com/<owner>/<repo>` (with or without `.git`),
and the GitHub CLI has to be on the daemon's PATH and signed in, because the daemon calls GitHub's
API with the token `gh auth token --hostname github.com` prints. Running `gh auth login` and then
`gh auth setup-git` covers both the token and the push. A project whose work should act as another
GitHub account selects a credential profile for the software module on this node instead: a directory
of its own inside `modules-private/software/` in the node home, holding the account's token in a file
named `github-token`, selected with `coa project module select-profile <project> software <profile>`. The
open-pr and gated-merge steps then read the token from that file and nothing else, and pause naming
the setting when it holds none. The profile covers the GitHub API alone: clones, fetches, and pushes
still use this node's own git credentials, whatever profile is selected. The worktree step fetches the flow run's branch
from the remote before it checks it out, so a branch someone updated there since the node last
pushed it (a reviewer's "Update branch" or committed suggestion) is built on, and a branch deleted
there is pushed again. When the node's branch and the remote's each have commits the other lacks,
and the remote's is still the commit coanda last pushed for the task, the branch was rewritten in
the node's clone, as a rebase of your own leaves it, so the worktree step checks it out as it
stands and the build's push replaces the remote's. When the remote's is any other commit, the
worktree step pauses and names how to reconcile them by hand in the node's clone. A branch still
checked out in the worktree of a flow run that has ended or been stopped is taken over: the step waits
for that flow run to finish with it, removes its worktree, and checks the branch out for itself. The push is
guarded by a lease on what the node last fetched or pushed, so a branch someone else updates on the
remote while a flow run builds is never overwritten: the flow run pauses instead, and the way out is to stop
it and start a new flow run on the parcel, whose worktree step fetches the remote's commits. Every other
failure (changes left in the worktree, a remote in another form, no token, a push or a request
GitHub refuses) pauses the flow run at its open-pr step with the reason, and a resume performs the step
again.

When the build flow run ends, the merge flow follows it on the same task with no step taken by hand.
It inspects the pull request and merges it through GitHub's API, never through auto-merge, and only
when the pull request is open against the primary branch, its head is the commit last pushed for
the task (by the build, or by a later rebase lap, after which it is the rebased commit), that head
is not behind the primary branch, GitHub reports it mergeable, and every check the software
module's `required-checks` setting names has succeeded with no other check failed or still running. The merge then closes the
task as completed. While checks are still running it waits a minute and looks again, for
twenty-three minutes in all, less a minute for each rebase it made, and then pauses. A resume after
that pause looks once more and pauses again if a check is still running, so resume it once the
checks have finished. A project names no checks until you set them, and a merge pauses
until it does, because without names it cannot tell whether every check has reported yet. Name the
checks by the names GitHub shows for them; for coanda's own repository those are its two CI legs:

```bash
coa project module set coanda software required-checks '["build-test (ubuntu-latest)", "build-test (windows-latest)"]'
```

The checks given replace the setting's list, and `coa project module show coanda software` prints them.

When the head is behind the primary branch, the merge rebases it rather than land a tree CI never
ran, whatever its checks report. It checks the branch out in a worktree of its own, rebases the
commit last pushed for the task onto the primary branch as a fresh fetch brings it, and pushes the result
with a lease that expects that commit on the remote. Then it waits a minute and inspects the pull
request again, holding it to the rebased commit and waiting for the checks on it, as it did on the
commit before. No review runs again: a rebase that applies cleanly is not a code change anyone
judges. A merge rebases at most three times, and a head still behind after that pauses. The merge
compares the head with the primary branch again right before it merges, but GitHub's merge takes no
expected base, so a commit that lands on the primary branch in the moment between that comparison
and the merge is still merged onto.

A rebase that stops on a conflict is aborted, the branch and the remote are left as they were, and
the merge pauses naming the files that conflict. Resolving a conflict is a code change, so it goes
through build, whose gates and both reviews run on it. Stop the merge flow run. Add a worktree of
`coanda/<short id>` from the node's clone, which the pause names, at a path of your own outside the
daemon's work paths; the clone is bare, so this is where you work. There, fetch the primary branch
and rebase the branch onto it, resolving the conflicts, without pushing. Remove that worktree, and
move the task back to build. The build's worktree step checks out the branch you rebased, because
the remote still holds the commit coanda last pushed, and the build gates, reviews, and pushes it,
and a merge follows again. The rebase also pauses, asking you to stop the flow run, when its worktree
has changes, when the branch holds a commit neither the build nor an earlier rebase pushed, or when
someone else updated the branch on the remote.

The lifecycle needs no configuring. The software engineering lifecycle is installed by default.

Agent steps run on Claude Code. With no configuration at all, the daemon runs each agent step as a
headless session of the `claude` executable it finds on the PATH, under whatever login that
executable already has, so install Claude Code and sign in to it before a flow run reaches its first
agent step. What a node runs its agent steps with is configured once per node, in `node.json` in the
node home: the executor (`claude-code`, with an optional path to the `claude` executable), and the
credential profiles a project can name with `coa project set-profile`.
[Node configuration](node-configuration.md) describes the file. An agent step's session can run `coa` itself, as a
principal bound to its own flow run; [how a session talks back](node-configuration.md#how-a-session-talks-back)
names the variables it is given. The repository's Rider run
configurations use `.coanda-dev` as their node home, and its checked-in `node.json` names the
`scripted` executor, so an agent step run from them pauses at no cost instead of starting a Claude Code
session; change that file to `claude-code` to run real sessions there. The worktree, gates, and
open-pr steps are real even there: a build flow run fetches from the project's remote, runs its build and
test commands, and pushes and opens its pull request on GitHub, so the prerequisites above apply to
that home too. A flow run there pushes a real `coanda/<short id>` branch and opens a real pull request on
the project's remote, and once the checks the project names pass, the merge that follows merges it,
so register a scratch repository in that home rather than one whose pull requests other people watch.

If you ran any build from before the stage and step names, its record does not carry over, and the
daemon refuses to start on it, naming the file. Move the home aside (for example
`mv ~/.coanda ~/.coanda.pre-stage`) and run the commands above again; a fresh home is created on
the first start. The commands changed with the names: `coa flow list` lists the flows,
`coa flow start <flow> --parcel <id>` starts a flow run, `coa flow-run list` and
`coa flow-run show` read flow runs, and `coa parcel add --stage <stage>` and
`coa parcel move <id> <stage>` name a stage. The API serves flows under `/flows` and flow runs under
`/flow-runs`, so a script written against the earlier names needs the new ones;
[the naming notes](design/naming.md#commands-and-the-api) map the old names to these, and
[the overview](overview.md#operating) has the full list of commands.
