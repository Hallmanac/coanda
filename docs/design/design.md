# Coanda: the system we would build today

Written 2026-11-03 11:21 EST by the start-over exploration session, from the rulings Brian gave at
the terminal on 2026-11-02 and 2026-11-03. The journal beside this file records each ruling with its
time; this document is the consolidated design. The previous version is kept as `design.md.*.prev`.
Working name: Coanda (from the Coanda effect). Path command: `coa`.

## 1. The answer in one paragraph

Coanda is a local-first work system whose product is contours: agentic loops a user composes from
agents and actions, each followed by flows that triggers start and a daemon runs, acting on units of
work called parcels that move through user-definable lifecycles. It ships with a software
engineering lifecycle (ideas through discovery, tasks through refine, build, review, closed) that a
new project gets with nothing configured. It replaces hall9k at parity and then goes past it,
because hall9k's engines become data, its store becomes one SQLite file per node, its transport
becomes one signed event stream per node, and its vocabulary is reduced to parcel, type, contour,
flow, agent, action, trigger.

## 2. What it is, and what it refuses

It is a work system. hall9k's scope line "not a task manager" is overturned: Coanda holds the
record of work and is the source of truth; Jira, Linear, and GitHub issues are optional sync
adapters fed from it and feeding into it.

It refuses to be a programming language. Definitions route; they do not compute. If a decision
needs reasoning, it is an agent (a judgment). If it needs a deterministic effect, it is an action in
C#. Nothing else exists. Customization lives at exactly three seams, parcel types, contours, and
agents, each a validated table the daemon loads, never code the daemon runs. A need beyond those
three is a new core action, not a fourth seam.

It refuses to make a new user configure anything. The shipped lifecycle is the product, not a
template; customization is a deliberate separate act, absent from the board, the default CLI
output, and the first run. The measure is cold install to first merged pull request, and it must
not grow as the system gains features.

It refuses to depend on git. Git is the default carrier for the ledger and the default code host,
and both are seams.

## 3. What is kept from hall9k

Everything hall9k learned carries over; what changes is where it lives and how it is expressed.
Kept as kernel behaviour: hash arbitration for claims, the generation fence (local to a node),
worktree per build, adopt-on-restart, pause with a lever, two-lens fresh-context review, the
pre-approved gated merge with the head-commit guard, the decisions and lessons record, two-tier
identity with three replication scopes, vouches, revocation, and succession by ranked keys.
Kept as doctrine: prescriptive lifecycle and permissive judgment, now raised one level (section 8);
tools before tokens; hall9k is the source of truth and the tracker is its outflow; a rule carries
the incident that created it; an unobserved fact is recorded as unknown.

The decision ids cited in the previous design (eb2f7217, 36cb3ef9, d68e9eb8, fdf7a66f, 9ecfb6af,
961e9c78, 0d8fcc8a, 69c20c43, 91805bf3, 2ab40608, 3fd0a6a3, 4d0ef231, 3a30c363, 1d50bf2d,
beffe35f, f92f5a4a, 9293734f, 907b8a9a, e83d0a62, 3fc83b67, c98ba66d, 26ce8569, 2b82bcef, 139c0ddc,
6a31ee1d, d43ecbd2, f109e6cd) are the seed of Coanda's own decision record and migrate with their
original ids and dates.

## 4. The parts

1. The store: one SQLite database per node, Marten's API through Fisher, the daemon as sole writer.
2. The kernel: the meta-model for parcels, relations, claims, placement, budget, identity, and the
   core actions.
3. Definitions: parcel types, contours, agents, as aggregates in the store, with scopes and forks.
4. The interpreter: a pure function from a contour, the current position, and the last
   answer to the next move.
5. The executor seam: one interface to vendor agent runtimes, with a Claude Code adapter and a
   scripted test adapter.
6. The transport: signed per-node JSONL segments on a ledger home that is a seam.
7. The surfaces: window, CLI, rendered files, portal, MCP, all renderings of one registry.
8. Integrations: GitHub, Jira, Linear, compiled in behind one interface.

## 5. The store

One SQLite file per node in the node home. Marten's API through Fisher (JasperFx, MIT, Marten on
SQLite; v1.27.0 as of 2026-11-02, releases several times a week, formally supported). Events are
the truth; projections produce every view, including the tables a portal or desktop surface reads.
Project is a tenant key on every stream (conjoined tenancy), so a project is a filter, not a file;
node-level records (identity, nodes, credential profiles, fleet state) live beside project streams.

The daemon is the only writer. The CLI, the portal, MCP, and the relay call one loopback command
and query API on the daemon; the CLI starts the daemon when it is not running. This removes the
second-writer problem Fisher's guidance warns about and removes store and code generation from the
CLI, which becomes thin.

Conditions carried from the Fisher evaluation: pin the Fisher version; async projections carry
version constants; write ceremony (optimistic concurrency) only on the parcel, the flow,
and arbitration streams. hall9k uses none of the Marten features Fisher lacks (no compiled
queries, no Newtonsoft, no child-collection LINQ, no bus); the 32 raw Postgres JSON path strings
are rewritten.

Rejected: a hand-rolled SQLite event log (loses projections and replay); per-project files (needs
a second node database and merges across files); Postgres (a server to run for one user).

## 6. Transport and trust

Each node exports the events it authored, filtered by replication scope (private never leaves the
node; fleet goes to the owner's nodes; team goes to the project), as JSONL: one event per line,
in monthly segment files, on a ref named for the node, `refs/coanda/nodes/<node-id>`. Peers fetch,
read lines past their last imported sequence, and import idempotently on origin plus sequence. The
files are a wire format; SQLite is the truth. Events go on the wire once; there are no envelopes
and no transport bookkeeping events. Large artifacts (transcripts, logs, prompts, reports) are
never events, only referenced. Messages between nodes are events in the same stream.

Partial access inside a multi-repo project: every event carries the repositories its parcel
targets, or "project-wide" for membership, definitions, and decisions. Each repository's ref
namespace carries the project-wide events plus that repository's events; a node pushes to and
imports from every repository of the project it holds, and the import's idempotency on origin plus
sequence stores a parcel that spans repositories once. A teammate who holds one repository sees the
project's membership, definitions, and decisions, and that repository's parcels, nothing else. The
tag is do-now; multi-repository push and import arrive with multi-repo projects.

Volume, from hall9k's own record: 62 MB raw over about 80 days across two nodes; about 12 MB per
node per month before filtering, 2 to 3 MB packed; 25 to 35 MB per node per year in the repo.

The ledger home is a seam with three values: a ref namespace in the project's own repository
(default), a named git remote (for a project with no code repo or a team that wants separation),
or the relay. Its value is a replicated project setting. Because every node can regenerate its
segments for any destination from its own table, the hosting repository is a mailbox, not an
archive: a move command pushes to the new home, writes the new location, and leaves a forwarding ref
in the old one. Runbook rule: move before you archive.

Trust lives on the event, not the carrier. Each exported event carries origin, sequence, the hash
of the previous event from that origin, and an ed25519 signature over those plus the payload (a
hash chain per node). A reader verifies any prefix and detects a rewind or fork by the chain
breaking. Trust is recomputed on import against the members and succession records. Git signing was
considered and withdrawn because the relay has no commit to sign.

Identity: one owner root, one key per node, vouches, root-only revocation, succession by ranked
keys exactly as hall9k's design (successor named, root rotated, successor revoked; an earlier key
outranks a later one; first rotation to land wins), carried over as events. An offline heir key is
a later option for the single-node owner.

The relay, when it exists, is a dumb multi-tenant pass-through with no retention. Whether it is
pure pass-through or an encrypted store-and-forward buffer it cannot read is still open.

There are no locks or leases. Contention is settled by hash arbitration; the publish-and-fetch seam
and the generation fence stay local to a node.

## 7. Contours, flows, triggers, and the interpreter

A contour is a sequence of steps, and a flow is one execution of a contour on a parcel: the flow
follows the contour the way a Coanda flow follows the contour of a surface. A judgment step names an
agent and an answer schema; the daemon spawns the agent and validates its answer. An action step
names something only the daemon does: spawn, wait for a human, wait for an external event, timer,
emit outcome, move a parcel (the core, domain-free set); worktree, gates, open PR, gated merge,
rebase (the software set). Each step has transitions: a table of answer field equals value, visited
fewer than N times, go to step, else pause. Caps are mandatory per transition and per contour
(budget, spawns); the daemon refuses to load a contour without them. hall9k's task run is one
contour: build, three reviews, fix, two reviews, fix, until clean or capped. Its plan run is a
second. Idea discovery is a third. A contour may be one step. A flow pauses when it waits for a
person or an external event, or when a cap or its budget is reached; a paused flow is live and is
resumed or stopped, and a stopped flow is final.

A trigger is an event subscription: an event kind plus equality filters, nothing more. A parcel
entering a state on this node, a PR opened by a flow, a PR assigned to a person, a schedule tick, a
manual start, another flow's typed outcome. A trigger starts a flow of the contour it names. A
person may own a trigger (a subscription), which is what hall9k's personas become. If matching
needs judgment, the contour starts with a judgment step that may answer "not for me".

Contours compose: a contour declares a typed outcome (PR opened, report produced, parcels
derived), and the next contour follows it, so a flow ending with that outcome starts a flow of the
next contour on the same parcel, with a depth cap and a budget the chain shares.

The interpreter is a pure function: given the contour, the position, and the answer just
returned, it returns the next move (go to, pause, end). Every flow records the content hash of its
contour and of each agent it ran, so "did the new review contour help" is a query.

Brian ruled on these words on 2026-11-03: a contour is the stored definition; a flow is one
execution of a contour on a parcel, and it pauses and is resumed or stopped; an agent runs on a
model spec. naming.md records the words they replace, and "run" stays retired.

## 8. Parcels and types

A parcel is the unit of work a flow acts on and the place its state, record, and board row live; a
pause and its lever live on the parcel's flow, and the paused state of the lifecycle is a person's
pause of the parcel itself. A parcel has exactly one type and one state. A parcel type is a
definition declaring the lifecycle (states, allowed moves, entry states, exactly one terminal state
with a resolution set), the fields, and the bindings: a gate before a transition and a contour after
it. Those two extension points are the whole middleware story.

The kernel fixes the meta-model and nothing below it: identity and provenance, claim with hash
arbitration, placement on a node, budget, and generic relations (derived-from, blocked-by,
stacked-on, member-of). An epic is member-of with a name. A type declares which types it may
derive. Relations stay within a project for now; crossing projects waits until a real
dependency between separately owned projects appears.

The software bundle is two types. Idea, with a discovery lifecycle: open, discovery, paused, closed
(resolution split, concluded, or archived). Task, with a build lifecycle: refine, build, review,
paused, closed (resolution completed, abandoned, superseded, duplicate). Discovery either carries
the idea forward or derives task parcels and closes the idea as split; each derived task starts at
refine, as does a task created directly. Fields are gated: an objective and criteria to leave
refine, a PR to leave build. The build contour ends when its pull request opens: it opens the PR,
moves the task to review, and ends with the outcome pr-opened. The merge is its own contour that
follows, performing the gated merge with the head-commit guard and closing the task as completed as
its ending act. QA is a contour that follows the build contour's pr-opened outcome on the same
parcel, with its own depth cap and the chain's budget, and the merge contour follows QA's passing
outcome where a project runs QA, or pr-opened where it does not, so the PR waits on QA by chaining.
A contour bound to review starts only when a person moves the parcel there, never when the build
flow's own step does, because a bound entry on a parcel with a live flow starts nothing and is never
saved for later. A parcel with a live flow, running or paused, is closed by nobody and moved only
by that flow's own steps; a person stops the flow before moving or closing the parcel. The words
idea and task survive as type names; nobody relearns hall9k's vocabulary.
Nothing software-specific (branch, worktree, gate, PR) leaks into the meta-model or the interpreter;
those are fields of the software types and the software actions.

hall9k's task types map by one rule: a new parcel type when the lifecycle or the ending differs
(review, spike), a new contour when only the work differs (content), a field when only the prompt
varies.

Type versioning: a parcel records the content hash of its type at creation; a new type version
applies to new parcels; an explicit migrate command moves an existing parcel only when its current
state exists by name in the new version.

## 9. Definitions, scopes, and forks

Parcel types, contours, and agents are aggregates in the store, created and changed by commands with
a role-checked principal, replicated like any record. Files appear only for export, import, and an
editing round-trip. Decisive reason: a project outside software has no git repo to hold files.

Definitions carry a kernel-hardwired status: draft, published, retired. Only published governs
work; publishing needs an owner-role principal. This is how an agent proposes a contour change (a
draft) without it taking effect until a person publishes it. It is not a parcel lifecycle and is not
called one.

Three scopes, nearest wins by name: project (shared with teammates through the project ledger),
owner (fleet-replicated to every node and project the owner holds), shipped (carried by the binary,
read-only, versioned with the release). A customization of a shipped definition is a fork that
records its base name and version. Unforked users take new shipped versions automatically; forked
users get a notice and a three-way diff command, with fold-in an explicit act. Nothing is ever
silently replaced. A shareable template is an export of a set of definitions; import recreates
them.

An agent definition holds a persona prompt (role, purpose, output, rules, prohibitions), a model
spec (vendor, model, effort), a tool set, skills by name, MCP servers, and the answer schema.
Per-parcel context (criteria, journal, notes, code) is assembled by the daemon per step from
declared sources and never folded into the agent. Skills stay vendor-native repo files referenced
by name. Credentials are per node, never in the store.

## 10. The executor seam

One interface: spawn, collect, resume, send into a live session, stop, list, reattach after a daemon
restart. An adapter per vendor renders an agent definition into the vendor's invocation: persona to
system prompt, tool set to allowlist plus deny hook, MCP servers to the vendor's config, skills to
tier paths, model spec to flags; caps passed natively where the vendor has them, with the daemon's
timer authoritative. Every judgment ends in one JSON document validated against the step's schema;
invalid or missing is retried once by resuming the same session, then the flow pauses with the
transcript as the lever. The parcel keeps its state; its live flow is where the pause and the lever
are found, and a resume performs the step again with a new spawn. hall9k's fifteen marker-line
grammars do not exist.

Spawn takes a credential profile. A node holds named profiles per vendor; a project carries a
node-local, never-replicated setting naming its profile; a binding may override per agent. For
Claude Code a profile is a config directory (subscription login, via `CLAUDE_CONFIG_DIR` on the
spawned process, documented for multiple accounts) or an API key helper. Subscription profiles
cannot use bare mode; the Agent SDK may not use subscription logins at all, which confirms the CLI
adapter as primary.

Measurement on every spawn, as events on the parcel: contour hash, agent hash, model spec, tokens,
cost, turns, duration, answer.

Two adapters in the box: Claude Code headless, and a scripted adapter returning canned answers so
contours and the interpreter are tested without a vendor or a network.

## 11. Surfaces

One registry of commands, queries, and actions, each with a schema, allowed principals, and a
side-effect flag. Every surface is a rendering of it, role-scoped.

The window: an interactive session in the project home holding the owner principal, the operator's
primary surface. It reads, rules, and starts; it never drives a lifecycle a flow is driving, and it
spawns nothing itself (discovery, challenge, walks are contours whose flows the daemon runs). Its
recipe is a stored agent definition: a hand edit is a draft and a publish, a regeneration is a draft
to diff. The courier is a trigger on feed events bound to a deliver-to-window action, pushing into
the window's live session through the vendor's inbound channel, with the batching wait and the
bypass list as binding parameters.

The CLI, `coa`: noun-first (parcel, contour, flow, agent, project, node), thin, calls the daemon and
starts it when needed; verbs follow the registry. Sessions talk back through it, with their
principal in a token the adapter places in the environment.

Rendered files (task.md, prompt.md, decisions.md, lessons.md) are projections written at spawn,
never truth, never edited.

The portal: a local page served by the daemon on the loopback, for attention and walks first, not
a kanban; reachable through the relay later. MCP: the registry over loopback streamable HTTP with a
stdio shim, last.

## 12. Integrations

GitHub, Jira, Linear compiled into the daemon behind one interface, both directions: inbound
external events as triggers (issue assigned, PR assigned), outbound status as a projection the
adapter pushes. A status mapping table per tracker sits on the parcel type; shipped types carry
all three, a custom type carries what its author writes. Coanda is the truth in both directions.
An out-of-process adapter shim is possible behind the same interface later.

The pre-approved merge is the daemon's own: inspect the PR, then merge with the head-commit guard
and the skew check. GitHub's auto-merge feature is not used, because it would merge a head pushed
after the last inspection.

## 13. Operations

One self-contained binary per platform; the daemon and the CLI are one executable. Install is
download and run; the SQLite file is created on first start in the node home. Update fetches the
next GitHub release and restarts when no step is in progress, reattaching live sessions. Doctor
checks git, gh, the agent CLI, and the node key. The daemon runs as a user process started on
demand; service registration is later. Windows through the same process-manager seam. One local-
time daemon log per node, plus the measurement events, which are what gets queried.

## 14. Beyond software

Not built now, designed for: a business defines a parcel type and a lifecycle for its own work and
its agents reach its systems through MCP servers named on the agent definition. What that costs
today is only the no-leakage rule (section 8), MCP servers on agent definitions, and keeping the
core actions domain-free. Data from an external MCP server is untrusted text, read only in a
judgment step with a narrow tool set, never allowed to steer a step with write power.

## 15. Priorities

Do-now (the box): store, registry, thin CLI, parcel meta-model and relations, two shipped types,
derive and split, definitions as aggregates with status and scopes and the fork-base field,
interpreter with caps, triggers, typed outcomes with depth and budget caps, executor seam with both
adapters, resume and reattach, measurement events, rendered files, signed JSONL export and import
with the hash chain, arbitration, join, identity event shapes including succession, window with
stored recipe, discovery and build and review-on-assignment contours, gated merge, operations
(binary, install, update, doctor), Windows in the last week, per-project ledger home setting,
multi-repo project fields, credential profile field.

Fold-in (after the box, in order): Jira and Linear, succession commands, portal, ledger move and
forwarding ref, credential profile commands, fork notices and three-way diff, courier binding,
challenge and walk contours, export and import of definitions, type migrate command, service
registration, MCP.

Deferred: cross-project relations, relay (and its encryption question), template sharing between
people, other vendors, out-of-process adapters, user-authored skills in the store, business
lifecycles, offline heir key.

## 16. Rejected along the way

Files in the repo as definitions (no repo outside software; loses store versioning). A separate
ledger repository (two repos per user). One parcel type with phase-gated fields (made split a
hack). A separate lifecycle aggregate (dissolved into the parcel type). Definitions as parcels
(recursion). Git-native signing (no commit on the relay). GitHub auto-merge (no head guard). A
fixed lifecycle (Brian: building a lifecycle is a feature). Per-project SQLite files. The Agent SDK
as the primary adapter. Hardwiring idea and task in C# for the box (a meta-model cannot be
retrofitted under live data). The portal as the box's primary surface. A service installer in the
box. Windows earlier than the last week.
