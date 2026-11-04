# Coanda prototype plan

Written 2026-11-03 11:21 EST from the rulings in `journal.md`; replaces the plan of 2026-11-02, kept as
`prototype-plan.md.*.prev`. The design is `design.md`. This plan is read by the coanda project's
orchestrator window; Brian stays hands-off beyond the four duties in section 5.

## 1. What the box proves

Coanda is a full replacement for hall9k. The box is the first step, not the destination: three
weeks, hard stop at four, that prove the store, the definitions-as-data loop layer, the transport,
and the second node. Everything after it is parity work in the order of section 8.

Exit scenario. On the Mac, from a cold install of one binary, a project is registered on the coanda
repository with nothing configured. An idea parcel goes through a flow run of the discovery flow and
derives a task parcel at refine with criteria. The build flow is read from the stored shipped
definition, and a flow run of it runs the task: worktree, build agent step, gates system step, two review
agent steps, and a fix lap; it opens the pull request, moves the task to review, and ends with the
outcome pr-opened. The merge flow follows that outcome: its flow run performs the gated merge with
the head-commit guard, and the parcel closes with resolution completed. A second node joins by
cloning the repository, verifies the hash chain, claims a parcel through hash arbitration, and runs
the same flows. The window, launched from its stored recipe, rules on one pause. Every spawn has
its measurement events. No file in a project home is edited by hand.

## 2. Repository and stack

Local: `/Users/brianhallmanac/code/coanda`. GitHub: `Hallmanac/coanda`, private through the box,
public at the first release (the ledger refs go public with it, as hall9k's do). License: Apache
2.0 with NOTICE, in the first commit with a one-paragraph README. Created only on Brian's go at a
terminal.

Stack: C# on .NET 10, one self-contained executable per platform that is both daemon and CLI
(`coa`). Projects: `Coanda.Core` (meta-model, definitions, interpreter, registry), `Coanda.Daemon`
(store, loops, adapters, transport), `Coanda.Cli` (thin), `Coanda.Tests` (unit on the scripted
adapter and an in-memory Fisher store; no git integration tests, a unit test over the generated
command sequence is the ceiling). Packages pinned centrally; Fisher pinned to one version.

Seed decisions: hall9k's decision record migrates in phase 4 with original ids and dates; until
then the coanda repository carries `decisions.md` rendered from the list in design.md section 3.

## 3. Phases and gates

| Phase | Builds | Gate | Built by |
|---|---|---|---|
| 0 Skeleton | Repository, solution, build and test gate, CI on Mac and Windows runners, the binary, the Fisher store with tenancy, the registry with principals, the thin CLI that starts the daemon, a parcel aggregate with the two shipped types as data | A parcel is created and shown through `coa` and survives a daemon restart | One session from the hall9k project window, by hand |
| 1 Definitions and interpreter | Parcel types, flows, agents as aggregates with draft and published and scopes and the fork-base field; the routing-only interpreter; triggers; typed outcomes with depth and budget caps; the scripted adapter; core system steps | A flow run of the shipped build flow runs end to end on canned answers with every cap enforced and refused at load without them | hall9k tasks dispatched on the Mac node |
| 2 Real sessions | Claude Code adapter with profiles, resume, reattach, rendered files, the software system steps (worktree, gates, open PR, gated merge, rebase), measurement events | The build and merge flows land a merged PR on the coanda repository | hall9k tasks |
| 3 Replication | Signed JSONL export and import by scope, the hash chain, verification on import, members and vouches and succession event shapes, arbitration, join, ledger home setting | The second node (the Windows machine, Brian's call on 2026-11-03 11:26 EST) clones, verifies, claims by arbitration, and builds | hall9k tasks; from this gate Coanda builds itself |
| 4 Judgment | Discovery flow, review-on-assignment flow (one step), window recipe as a stored agent definition, courier push, measurement queries, migration of decisions and lessons | The exit scenario minus Windows | Coanda |
| 5 Platform | Windows through the process seam, update, doctor, release pipeline with all assets | The exit scenario on Windows | Coanda |

Estimated size: phase 0 about 4,000 lines; phases 1 to 5 about 20,000 more; the box about 25,000
lines of source. Parity after the box is a multiple of that and well under hall9k's 277,000
source lines, because engines become definitions, grammars become schemas, envelopes vanish, and
the CLI loses its logic.

## 4. How the work runs

hall9k builds Coanda from phase 1. Coanda is registered as a hall9k project on the Mac node; each
phase is a set of hall9k tasks whose criteria come from this plan; hall9k's daemon builds,
reviews, fixes, opens the PR, and merges under Brian's existing pre-approval rules. A hall9k
project orchestrator window for coanda walks and challenges drafts under the standing
go-with-recommendations rule, publishes, and surfaces only pauses. From phase 3's gate, Coanda's
own build flow takes its own tasks, and hall9k's role shrinks to the window until sunset. There is
no separate loop; the daemon is the loop.

## 5. What Brian does

1. Say go on the repository, at a terminal.
2. Open one window for the coanda project once its recipe exists.
3. Rule on the pauses the courier brings.
4. Name the second node for phase 3 and the Windows week for phase 5.

## 6. Measures

Recorded from phase 2 on: cold install to first merged PR (minutes, Brian's clock); tokens and
cost per merged PR; agent steps per merged PR; pauses per merged PR and who resolved them; flow
hash and agent hash on every instance; rediscovered hall9k decisions per day (anything re-ruled
that the migrated record already held).

## 7. Kill criteria

Stop and write up if any holds: day 28 arrives without phase 3's gate; after day 14 more than one
hall9k decision per day is being rediscovered rather than carried; replication on the git path
turns out to need a catch-up protocol; the interpreter needs an expression beyond equality and a
visit count to express a shipped flow.

## 8. After the box, in order

Jira and Linear adapters; succession commands; portal; ledger move and forwarding ref; credential
profile commands; fork notices and three-way diff; courier binding parameters; challenge and walk
flows; export and import of definitions; type migrate command; service registration; MCP last.

## 9. Replacement set (phase 6)

Before sunset the repository carries what hall9k's does: README, LICENSE and NOTICE, CONTRIBUTING,
CODE_OF_CONDUCT, SECURITY, SUPPORT, AGENTS.md, the window recipe, and a docs folder with getting
started, install, concepts, CLI map, operations, and scope, plus Mac and Windows setup guides.
Written by Coanda's own content flow where it can. README rule (Brian, 2026-11-03 11:26 EST): the README stays
short and for humans; all documentation lives in the docs folder; the README carries one "agents
start here" line pointing at an in-depth overview written for an agent that installs and operates
Coanda.

## 10. Migration and sunset

Decisions and lessons migrate as events with original ids and dates in phase 4. Open ideas become
idea parcels at open. Draft and queued tasks become task parcels at refine. Tasks already under way
finish on hall9k. Closed history stays in the hall9k repository, which is archived with a pointer.

Sunset criterion: Coanda has run both nodes' real work for two weeks with no fall back to hall9k,
the replacement set is complete, and the migration has landed. Then hall9k's daemons stop.

## 11. hall9k during the box

Feature freeze: work already under way finishes, no new drafts are published, fixes only, and hall9k keeps
dispatching Coanda's phases 1 and 2.

## 12. Out of scope for the box

Relay, portal, MCP, Jira and Linear, cross-project relations, business lifecycles, other vendors,
template sharing, service installers.

## 13. Risks

Fisher is young and has one releaser; mitigated by pinning and by the Marten API being the same,
so Postgres remains a fallback store behind the same code. Definitions-as-data may tempt an
expression language; the kill criterion in section 7 is the guard. Two adapters hide
vendor-specific behaviour (inbound messages, bare mode, structured output) that the seam must name
as capabilities rather than assume. The hash chain is new code on the critical path; it is small,
and the second node's join is the test.
