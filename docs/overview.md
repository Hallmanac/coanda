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
- flow: one execution of a contour on a parcel. A flow follows its contour; it can pause (waiting
  for a person, an external event, or a budget) and is then resumed or stopped.
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
- `coa parcel add`, `coa parcel list`, `coa parcel show <id>`, `coa parcel move <id> <state>`,
  `coa parcel close <id>`
- `coa contour list`, `coa contour start <contour> --parcel <id>`
- `coa flow list --parcel <id>`, `coa flow show <id>`

The record lives at `~/.coanda/coanda.db`. Do not edit it by hand.

A build from before 2026-11-03 used other words for contours, flows, and pauses, and other
commands for them; `docs/design/naming.md` maps the old commands to the ones above. The record that
older build wrote does not carry over, and the daemon refuses to start on it, naming the file. Move the old node home aside (for example
`mv ~/.coanda ~/.coanda.pre-rename`) and start again; a fresh home is created on the first start.
