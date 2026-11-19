# Coanda first version plan

Written 2026-11-03 11:21 EST from the rulings in `journal.md`; replaces the plan of 2026-11-02, kept as
`prototype-plan.md.*.prev`. Amended on 2026-11-04 to place the module work that design.md section
14.12 orders and to rename the first scope. Amended on 2026-11-18 from the rulings on the core-first
re-plan: the time statement and the dated kill criterion are dropped, phases 4 to 7 are re-phased
(section 3), decisions and lessons migrate as parcels of their own types, and the text says that
hall9k still builds Coanda's tasks. The design is `design.md`. This plan is read by the
coanda project's orchestrator window; Brian stays hands-off beyond the four duties in section 5.

## 1. What the first version proves

Coanda is a full replacement for hall9k. The first version is the first step, not the destination:
it proves the store, the definitions-as-data loop layer, the transport, and the second node.
Everything after it is parity work in the order of section 8.

Exit scenario. On the Mac, from a cold install of one binary, a project is registered on the coanda
repository with nothing configured. An idea parcel goes through a flow run of the discovery flow and
derives a task parcel at refine with criteria. The build flow is read from the stored shipped
definition, and a flow run of it runs the task: worktree, build agent step, gates system step, two review
agent steps, and a fix lap; it opens the pull request, moves the task to review, and ends with the
outcome pr-opened. The merge flow follows that outcome: its flow run performs the gated merge with
the head-commit guard, and the parcel closes with resolution completed. A second node joins by
cloning the repository, verifies the hash chain, claims a parcel through hash arbitration, and runs
the same flows. The window, launched from its stored recipe, rules on one pause. Every spawn has
its measurement events, and its prompt and transcript are kept as files whose hashes its events
record. No file in a project home is edited by hand.

## 2. Repository and stack

GitHub: `Hallmanac/coanda`, public since 2026-11-06 (the ledger refs are public with it, as
hall9k's are). License: Apache 2.0 with NOTICE, in the first commit with a one-paragraph README.
Created only on Brian's go at a terminal.

Stack: C# on .NET 11, one self-contained executable per platform that is both daemon and CLI
(`coa`). Projects: `Coanda.Core` (meta-model, definitions, interpreter, registry), `Coanda.Daemon`
(store, loops, adapters, transport), `Coanda.Sdk`, `Coanda.Modules.Software`, `Coanda.Cli` (thin),
`Coanda.Tests` (unit on the scripted adapter and an in-memory Fisher store; no git integration
tests, a unit test over the generated command sequence is the ceiling). `Coanda.Sdk` is the module
SDK, which references no other Coanda project: the module API's contract types, the interface an
in-process module answers through, a client for the core's loopback API, and the command runner and
git wrapper the daemon also uses. `Coanda.Modules.Software` is the software engineering module: its
worktree, gates, open-pr, gated-merge, and rebase steps, its GitHub client and worktree handling, and the
definitions folder of its types, flows, and agents, with the primary branch, build and test commands,
and required checks as its settings rather than the project's. Packages pinned centrally; Fisher pinned to one version.

Seed decisions: hall9k's decisions and lessons migrate in phase 5, each as a parcel of its own type
that carries its original id and date as fields (section 10).

## 3. Phases and gates

| Phase | Builds | Gate | Built by |
|---|---|---|---|
| 0 Skeleton | Repository, solution, build and test gate, CI on Mac and Windows runners, the binary, the Fisher store with tenancy, the registry with principals, the thin CLI that starts the daemon, a parcel aggregate with the two shipped types as data | A parcel is created and shown through `coa` and survives a daemon restart | One session from the hall9k project window, by hand |
| 1 Definitions and interpreter | Parcel types, flows, agents as aggregates with draft and published and scopes and the fork-base field; the routing-only interpreter; triggers; typed outcomes with depth and budget caps; the scripted adapter; core system steps | A flow run of the shipped build flow runs end to end on canned answers with every cap enforced and refused at load without them | hall9k tasks dispatched on the Mac node |
| 2 Real sessions | Claude Code adapter with profiles, resume, reattach, rendered files, the software system steps (worktree, gates, open PR, gated merge, rebase), measurement events | The build and merge flows land a merged PR on the coanda repository | hall9k tasks |
| Carve-out (after 2, before 3) | The software engineering module's contents moved into a module project that references only the SDK, hosted in process by the daemon through the constrained adapter; the Module principal, the dispatch id on the flow run, module-owned fields, two-scope settings with credential profiles, and the project reduced to a name plus a repos list; the wire contract's JSON schemas, with the SDK checked against them; the flow run's work path, worktree release through the feed, and the task type's module-owned repos field; the test that the module references only the SDK and the test that the adapter's surface is the schemas' surface | The build and merge flows land a merged PR on the coanda repository with every software step reached through the constrained adapter, and both tests pass | hall9k tasks |
| 3 Replication | Signed JSONL export and import by scope, the hash chain, verification on import, members and vouches and succession event shapes, arbitration, join, ledger home setting, the event tag read at export from the field the parcel's type declares as its tag field (the task type's repos field), a retag being a field set on that field tagged with the old and the new repos (every repo carries the whole chain, and forwarding waits for multi-repo push and import) | The second node (the Windows machine, Brian's call on 2026-11-03 11:26 EST) clones, verifies, claims by arbitration, and builds | hall9k tasks |
| 4 The engine for modules | Core, daemon, and SDK work only, in three waves. The boundary wave: the boundary rule (design.md section 14.1.1) goes into the design; the vocabulary test and the reference test; the host that composes the modules moves into the executable's project, so the daemon references Core and the SDK alone; the default modules come from the node configuration; `coa project add` renders a module's settings from its describe answer; the worktree repair moves to the software module; context is fenced and capped; an agent that names an MCP server is refused; a project may register with no repos; the core rule that tells a reader how to register a project stops quoting the software module's `--branch` option; a session's parcel record is named for the parcel and its type rather than written as `task.md` for every type; and any other fix the rule finds against main lands in this wave too. The shape wave: the external id and the parcel id on the stored external event, for idempotent event recording; triggers as the fourth kind of definition, which a module ships, with a target and a cap; relations, and a type's list of what it may derive; a person's answer on a resume; three new context source kinds, with the origin a created parcel records; the confined member of an agent; the hashes of a step's prompt and transcript on its event; the wave ends with both nodes rebuilt once. The mechanism wave: the trigger loop's new behaviour, with a trigger that targets the parcel an event names and a matching event resuming a waiting step; the in-process module reporting events through the watch call and watch requests, the watching node, and the cap; deriving parcels and waiting on blockers; the human step; the context sources; the measurement queries by flow hash and agent hash; the courier push's mechanism, which is the attention query and event and the deliver-to-window step with backoff; the surface for definitions; skills a module ships; the feed from the beginning | A second module with no software noun in it, bringing a type, a flow, a trigger, an event kind, and a step, runs end to end on the scripted executor with no change to Core or the daemon made for it. In that run an event recorded twice makes one parcel, a second event resumes a waiting step, the flow derives two parcels, one waits on the other and resumes at its close, a person's answer is recorded and routed on, a step's context carries another flow run's answer, a parcel that a trigger created on a flow run's end reads that flow run's answer in its context, and a query by flow hash finds the run. The vocabulary test and the reference test pass. Confinement is proved apart from that run, by a test of what the Claude Code adapter writes for a confined agent. The gate run adds the shipped discovery flow deriving a task from an idea on the Mac and both nodes replicating on the new shapes | hall9k tasks |
| 5 Modules and content | The software module's flows, agents, steps, event kinds, and skills at the level of the exit scenario, among them the discovery flow and the review-on-assignment flow, whose first trigger on an outside pull request is cut only with five requirements from the phase 3 security walk and a confined reviewer among its criteria (the five are listed below the table); the window recipe as a stored agent definition, with the courier's binding; the record module, with the decision and lesson types and their migration; the evals module's first version. Tasks run in parallel wherever they touch different definitions | The exit scenario minus Windows | hall9k tasks |
| 6 Platform | Windows through the process seam, with the per-call git token; update, doctor, release pipeline with all assets; run ceilings; the module boundary on the wire: module mode, the HTTP and JSON contract, supervision with backoff and pausing, the feed over the wire, install from the release archive on first run, verification against the registry key and the owner's root key, and doctor checking modules. It may start while phase 5 is running | The exit scenario on Windows with the software engineering module installed as a separate artifact from the release archive, started and supervised by the daemon, verified against the registry key | hall9k tasks |

The phase 3 security walk named five requirements for the first flow that reads an outside pull
request, and phase 5's review-on-assignment trigger is cut with all five among its criteria: a
pull request from a fork is refused before any checkout; the reviewer gets no shell, or a deny list
the module declares; the reviewer reads the diff the daemon wrote; no GitHub login is in the
session's environment; and the rule that tells a session to follow the repository's instruction
files is off.

Phase 7 is the replacement set that section 9 lists. It has no row of its own: what it builds is that
list, and it is done when the list is complete, which section 10's sunset criterion asks for.

Estimated size: phase 0 about 4,000 lines; phases 1 to 6 about 20,000 more; the first version about
25,000 lines of source. Parity after the first version is a multiple of that and well under hall9k's
277,000 source lines, because engines become definitions, grammars become schemas, envelopes vanish,
and the CLI loses its logic.

## 4. How the work runs

hall9k builds Coanda from phase 1, and it still builds Coanda's tasks. Coanda is registered as a
hall9k project on the Mac node; each phase is a set of hall9k tasks whose criteria come from this
plan; hall9k's daemon builds, reviews, fixes, opens the PR, and merges under Brian's existing
pre-approval rules. A hall9k project orchestrator window for coanda walks and challenges drafts
under the standing go-with-recommendations rule, publishes, and surfaces only pauses. There is no
separate loop; the daemon is the loop.

## 5. What Brian does

1. Say go on the repository, at a terminal.
2. Open one window for the coanda project once its recipe exists.
3. Rule on the pauses the courier brings.
4. Name the second node for phase 3, and name the week phase 6's Windows work runs in, since no
   date binds the phases.

## 6. Measures

Recorded from phase 2 on: cold install to first merged PR (minutes, Brian's clock); tokens and
cost per merged PR; agent steps per merged PR; pauses per merged PR and who resolved them; flow
hash and agent hash on every instance; rediscovered hall9k decisions per day (anything re-ruled
that the migrated record already held).

## 7. Kill criteria

Stop and write up if any holds: after day 14 more than one hall9k decision per day is being
rediscovered rather than carried; replication on the git path turns out to need a catch-up
protocol; the interpreter needs an expression beyond equality and a visit count to express a
shipped flow.

These criteria stay as the plan of 2026-11-03 wrote them, so day 14 counts from that date. Until the
record migrates in phase 5, a rediscovered decision is one re-ruled that hall9k's own decisions
record already held.

## 8. After the first version, in order

First, before any module that is not in the release archive: the owner-signed module path with
per-node acceptance, module sharing through a ref, module export and install from a file, and the
credential profile commands.
Then Jira and Linear adapters; succession commands; portal; ledger move and forwarding ref; fork
notices and three-way diff; courier binding parameters; challenge and walk flows; export and import
of definitions; type migrate command; service registration; MCP last.

## 9. Replacement set (phase 7)

Before sunset the repository carries what hall9k's does: README, LICENSE and NOTICE, CONTRIBUTING,
CODE_OF_CONDUCT, SECURITY, SUPPORT, AGENTS.md, the window recipe, and a docs folder with getting
started, install, concepts, CLI map, operations, and scope, plus Mac and Windows setup guides.
Written by Coanda's own content flow where it can. README rule (Brian, 2026-11-03 11:26 EST): the README stays
short and for humans; all documentation lives in the docs folder; the README carries one "agents
start here" line pointing at an in-depth overview written for an agent that installs and operates
Coanda.

## 10. Migration and sunset

Decisions and lessons migrate in phase 5 as parcels of their own types, the decision type and the
lesson type that the record module ships, not as events; each parcel carries its original id and
date as fields. Decision 833d8902 calls this the phase 4 migration because it was written under the
earlier phase table; under the table in section 3 it is phase 5, and that is the phase the
decision's number means. Open ideas become idea parcels at open. Draft and queued tasks become task
parcels at refine. Tasks already under way finish on hall9k. Closed history stays in the hall9k repository, which is archived with a pointer.

Sunset criterion: Coanda has run both nodes' real work for two weeks with no fall back to hall9k,
the replacement set is complete, and the migration has landed. Then hall9k's daemons stop. The
criterion excludes a project whose team stays on hall9k: that project stays on hall9k until its team
moves to Coanda together, and there is no bridge between the two.

## 11. hall9k during the first version

hall9k is not frozen: it keeps changing while it builds Coanda's tasks. Each gate sweep sorts
hall9k's changes since the last sweep by layer, as engine, module and flow content, or hall9k's own,
so that what hall9k learns reaches the layer of Coanda that should carry it. Coanda's discovery
and plan flows are read against hall9k's `h9k plan` work, which is their running specification. That
work takes a draft to published through headless refinement, a challenge, and applying the verdict,
and parks it when it cannot finish. It starts a plan from an idea with one headless discovery
session, a report in the idea's workspace, a gate where a person says pursue or stop, and one draft
cut per task line of the report. It holds a task until a check of what its blockers learned has
run, then refines it, challenges it once, and requeues it. And discovery, refinement, and the
challenge judge a feature draft by the smallest increment useful on its own.

## 12. Out of scope for the first version

Relay, portal, MCP, Jira and Linear, cross-project relations, business lifecycles, other vendors,
template sharing, service installers.

## 13. Risks

Fisher is young and has one releaser; mitigated by pinning and by the Marten API being the same,
so Postgres remains a fallback store behind the same code. Definitions-as-data may tempt an
expression language; the kill criterion in section 7 is the guard. Two adapters hide
vendor-specific behaviour (inbound messages, bare mode, structured output) that the seam must name
as capabilities rather than assume. The hash chain is new code on the critical path; it is small,
and the second node's join is the test.
