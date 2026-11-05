# Overview for agents

Read this page in full before installing or operating Coanda on someone's behalf.

## What Coanda is

A local-first work system. One binary, `coa`, is both the command line and the daemon. The daemon
is the only writer to the record, which is one SQLite file per machine (a node) under the user's
home directory. Every command you run goes through the daemon; `coa` starts it when it is not
running.

## Vocabulary

[naming.md](design/naming.md) is where the vocabulary is settled; this section names the words an
operator meets most, as naming.md defines them.

- parcel: the unit of work a flow run acts on. A parcel has exactly one type and stands at one
  stage of that type's lifecycle.
- type: the definition of a kind of parcel: its lifecycle, its fields, and its bindings.
- lifecycle: the stages a parcel of a type moves through and the moves allowed between them, with
  one or more entry stages, a paused stage, and one terminal stage called closed, reached with a
  resolution.
- stage: one position in a type's lifecycle, such as refine, build, or review.
- flow: the stored definition of steps, the transitions between them, and the caps on them, which
  a flow run follows.
- flow run: one execution of a flow on a parcel, always the two words.
- agent: a configured AI worker: a persona, the vendor, model, and effort it runs on, its tools,
  and the answer schema it answers in.
- agent step: a step in which the daemon spawns an agent and records the answer it returns.
- system step: a step the daemon performs itself with no agent (worktree, gates, open PR, merge,
  timer, wait).
- trigger: what starts a flow run of a flow: an event kind plus equality filters.
- paused: set aside, with a reason and who paused it. A flow run pauses when it waits for a person
  or an external event, or when one of its caps (visits, spawns, depth) or its budget is reached,
  and a paused flow run is resumed or stopped. A person pauses a parcel by moving it to its type's
  paused stage, which records who moved it but, as yet, no reason.

Two types ship installed: idea (open, discovery, paused, closed) and task (refine, build, review,
paused, closed).

## Installing

Phase 0: build from source with the .NET 10 SDK (`dotnet build`), then run
`src/Coanda.Cli/bin/Debug/net10.0/coa`. Release binaries follow in phase 5.

## Operating

- `coa daemon start` / `coa daemon stop` / `coa daemon status`

  `coa` sends nothing to a running daemon from a different build. Every other command refuses,
  naming both versions; run `coa daemon stop` and then the command again. `coa daemon status` and
  `coa daemon stop` work against a daemon of any version, and `coa daemon start` refuses to start
  beside one from another build.

- `coa project add <name> --repo <url> --branch <name> [--build <command>] [--test <command>]`,
  `coa project show <name>`,
  `coa project list`, `coa project set-profile <name> <profile>`

  `coa project add` registers a project with one repo and records `--branch`, `--build`, and
  `--test` as the software module's settings for it. Every later change to a module's settings,
  the required checks included, goes through `coa project module`.
- `coa project module set <project> <module> <name> <value>`,
  `coa project module select-profile <project> <module> <profile>`,
  `coa project module show <project> <module>`
- `coa parcel add`, `coa parcel list`, `coa parcel show <id>`, `coa parcel move <id> <stage>`,
  `coa parcel close <id>`
- `coa flow list`, `coa flow start <flow> --parcel <id>`
- `coa flow-run list --parcel <id>`, `coa flow-run show <id>`, `coa flow-run pause <id> --reason <why>`,
  `coa flow-run stop <id> --reason <why>`, `coa flow-run resume <id>`

A parcel with a live flow run, whether that flow run is running or paused, is closed by nobody and moved
only by that flow run's own steps. `coa parcel move` and `coa parcel close` refuse it, naming the flow run
that holds it; the flow run has to end or be stopped first. `coa flow-run stop <id> --reason <why>` stops
it and frees the parcel, and `coa flow-run resume <id>` sets a paused one going again instead. A
flow that declares `closes` is the one way a flow run closes its parcel: when a flow run of it ends, the
flow run's end, the release of the parcel, and its close with that resolution are recorded together.
The shipped merge flow declares `closes` as completed, and so does any flow a project
publishes with `closes` set, a flow that replaces the shipped merge included. When several
flows follow the same end, they start one at a time in name order, except that the ones that
declare `closes` start after every one that does not, so a closing follower never shuts the parcel
on a follower still waiting to start.
`coa flow-run pause <id> --reason <why>` pauses a running flow run without freeing the parcel, and a resume
then performs the step it was paused at again.

The record lives at `~/.coanda/coanda.db`. Do not edit it by hand. `coa record export` writes a full
export of it, in current shapes, to an owner-only file under the node home, and
`coa record rebuild <export>` rebuilds the record from one, moving the old database aside; both refuse
while a daemon runs. [Rebuilding the record](rebuilding-the-record.md) gives the steps. The node's executor and
credential profiles live in `~/.coanda/node.json`, described in
[node configuration](node-configuration.md).

A flow run's agent steps need Claude Code. With no `node.json`, the daemon runs each agent step as a real
session of the `claude` executable on the PATH, under whatever login that executable already has,
and that session's spend is billed to that login. Install Claude Code and sign in to it before a
flow run reaches its first agent step; without it, the agent step pauses saying why. To exercise flow runs
with no sessions and no spend, write a `node.json` naming the `scripted` executor, under which
every agent step pauses.

A build flow run's worktree and gates steps start real processes. The daemon needs git 2.31 or later
and the project's build and test tools on its PATH, and it has to reach the project's remote with
no prompt: every git command runs with `GIT_TERMINAL_PROMPT=0` and `GIT_SSH_COMMAND=ssh -o
BatchMode=yes`, so an ssh key needs no passphrase or has to be loaded in an agent, and a key named
only in `core.sshCommand` is not used. The gates step ends a build or test command still running
after an hour and fails, as it does for a nonzero exit. The open-pr step needs the project's remote
to be a github.com repository in one of the forms getting started lists, and the GitHub CLI on the
daemon's PATH and signed in. These steps are not simulated: a build flow run pushes a real
`coanda/<short id>` branch and opens a real pull request on the project's remote.
[Getting started](getting-started.md) has the details.

A build from before the stage and step names used other words for flows, flow runs, pauses, stages,
and steps, and other commands for them; `docs/design/naming.md` maps the old commands to the ones
above. A record written by any build from before the stage and step names does not carry over, and
the daemon refuses to start on it, naming the file. Move the old node home aside (for example
`mv ~/.coanda ~/.coanda.pre-stage`) and start again; a fresh home is created on the first start.
