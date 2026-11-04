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
- contour: the definition of an agentic loop: judgment steps (an agent) and action steps (something
  the daemon does), with transitions and caps.
- flow: one execution of a contour on a parcel. A flow follows its contour. It pauses when it
  waits for a person or an external event, or when one of its caps (visits, spawns, depth) or its
  budget is reached; a paused flow is resumed or stopped.
- agent: a definition of a persona, a model spec (vendor, model, effort), tools, and answer schema that a judgment step spawns.
- action: a deterministic step the daemon performs (worktree, gates, open PR, merge, timer, wait).
- trigger: an event subscription that starts a flow of a contour.

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
- `coa contour list`, `coa contour start <contour> --parcel <id>`
- `coa flow list --parcel <id>`, `coa flow show <id>`, `coa flow pause <id> --reason <why>`,
  `coa flow stop <id> --reason <why>`, `coa flow resume <id>`

A parcel with a live flow, whether that flow is running or paused, is closed by nobody and moved
only by that flow's own steps. `coa parcel move` and `coa parcel close` refuse it, naming the flow
that holds it; the flow has to end or be stopped first. `coa flow stop <id> --reason <why>` stops
it and frees the parcel, and `coa flow resume <id>` sets a paused one going again instead. The
one close made on a flow's behalf is the merge contour's own: the flow's end, the release of the
task, and its close as completed are recorded together.
`coa flow pause <id> --reason <why>` pauses a running flow without freeing the parcel, and a resume
then performs the step it was paused at again.

The record lives at `~/.coanda/coanda.db`. Do not edit it by hand. The node's executor and
credential profiles live in `~/.coanda/node.json`, described in
[node configuration](node-configuration.md).

A flow's judgments need Claude Code. With no `node.json`, the daemon runs each judgment as a real
session of the `claude` executable on the PATH, under whatever login that executable already has,
and that session's spend is billed to that login. Install Claude Code and sign in to it before a
flow reaches its first judgment; without it, the judgment pauses saying why. To exercise flows
with no sessions and no spend, write a `node.json` naming the `scripted` executor, under which
every judgment pauses.

A build flow's worktree and gates steps start real processes. The daemon needs git 2.31 or later
and the project's build and test tools on its PATH, and it has to reach the project's remote with
no prompt: every git command runs with `GIT_TERMINAL_PROMPT=0` and `GIT_SSH_COMMAND=ssh -o
BatchMode=yes`, so an ssh key needs no passphrase or has to be loaded in an agent, and a key named
only in `core.sshCommand` is not used. The gates step ends a build or test command still running
after an hour and fails, as it does for a nonzero exit. [Getting started](getting-started.md) has the details.

A build from before 2026-11-03 used other words for contours, flows, and pauses, and other
commands for them; `docs/design/naming.md` maps the old commands to the ones above. The record that
older build wrote does not carry over, and the daemon refuses to start on it, naming the file. Move the old node home aside (for example
`mv ~/.coanda ~/.coanda.pre-rename`) and start again; a fresh home is created on the first start.
