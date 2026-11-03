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
- flow: a definition of an agentic workflow: judgment steps (an agent) and action steps (something
  the daemon does), with transitions and caps.
- flight: one execution of a flow on a parcel.
- agent: a definition of a persona, model, tools, and answer schema that a judgment step spawns.
- action: a deterministic step the daemon performs (worktree, gates, open PR, merge, timer, wait).
- trigger: an event subscription that starts a flow.

Two types ship installed: idea (open, discovery, parked, closed) and task (refine, build, review,
parked, closed).

## Installing

Phase 0: build from source with the .NET 10 SDK (`dotnet build`), then run
`src/Coanda.Cli/bin/Debug/net10.0/coa`. Release binaries follow in phase 5.

## Operating

- `coa daemon start` / `coa daemon stop` / `coa daemon status`
- `coa parcel add`, `coa parcel list`, `coa parcel show <id>`

The record lives at `~/.coanda/coanda.db`. Do not edit it by hand.
