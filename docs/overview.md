# Overview for agents

Read this page in full before installing or operating Coanda on someone's behalf.

## What Coanda is

A local-first work system. One binary, `coa`, is both the command line and the daemon. The daemon
is the only writer to the record, which is one SQLite file per machine (a node) under the user's
home directory. Every command you run goes through the daemon; `coa` starts it when it is not
running.

## Vocabulary

- parcel: the unit of work. A parcel has exactly one type and one lifecycle state.
- type: the definition of a kind of parcel: its lifecycle (states, moves, one terminal state
  called closed with a resolution), its fields, and its bindings.
- flow: the definition of an agentic loop: judgment steps (an agent) and action steps (something
  the daemon does), with transitions and caps.
- flow run: one execution of a flow on a parcel. A flow run follows its flow. It pauses when it
  waits for a person or an external event, or when one of its caps (visits, spawns, depth) or its
  budget is reached; a paused flow run is resumed or stopped.
- agent: a definition of a persona, a model spec (vendor, model, effort), tools, and answer schema that a judgment step spawns.
- action: a deterministic step the daemon performs (worktree, gates, open PR, merge, timer, wait).
- trigger: an event subscription that starts a flow run of a flow.

Two types ship installed: idea (open, discovery, paused, closed) and task (refine, build, review,
paused, closed).

## Installing

Phase 0: build from source with the .NET 10 SDK (`dotnet build`), then run
`src/Coanda.Cli/bin/Debug/net10.0/coa`. Release binaries follow in phase 5.

## Operating

- `coa daemon start` / `coa daemon stop` / `coa daemon status`
- `coa project add <name> --repo <url> --branch <name> [--build <command>] [--test <command>]`,
  `coa project show <name>`,
  `coa project list`, `coa project set-profile <name> <profile>`,
  `coa project set <name> --required-check <check> [--required-check <check> ...]`
- `coa parcel add`, `coa parcel list`, `coa parcel show <id>`, `coa parcel move <id> <state>`,
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

The record lives at `~/.coanda/coanda.db`. Do not edit it by hand. The node's executor and
credential profiles live in `~/.coanda/node.json`, described in
[node configuration](node-configuration.md).

A flow run's judgments need Claude Code. With no `node.json`, the daemon runs each judgment as a real
session of the `claude` executable on the PATH, under whatever login that executable already has,
and that session's spend is billed to that login. Install Claude Code and sign in to it before a
flow run reaches its first judgment; without it, the judgment pauses saying why. To exercise flow runs
with no sessions and no spend, write a `node.json` naming the `scripted` executor, under which
every judgment pauses.

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

A build from before the flow name used other words for flows, flow runs, and pauses, and other
commands for them; `docs/design/naming.md` maps the old commands to the ones above. A record written
by any build from before the flow name does not carry over, and the daemon refuses to start on it,
naming the file. Move the old node home aside (for example `mv ~/.coanda ~/.coanda.pre-flow`) and
start again; a fresh home is created on the first start.
