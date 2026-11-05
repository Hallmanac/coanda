# Coanda: the system we would build today

Written 2026-11-03 11:21 EST by the start-over exploration session, from the rulings Brian gave at
the terminal on 2026-11-02 and 2026-11-03, and amended from the rulings of 2026-11-04: the concepts
walk, which settled the vocabulary and made Coanda a core plus modules, and the module design and
its challenge, which gave section 14 its present text and amended the sections that agree with it.
The journal beside this file records each ruling with its time; this document is the consolidated
design. The previous version is kept as `design.md.*.prev`.
Working name: Coanda (from the Coanda effect). Path command: `coa`.

## 1. The answer in one paragraph

Coanda is a local-first work system whose product is flows: agentic loops a user composes from
agent steps and system steps, each followed by flow runs that triggers start and a daemon runs,
acting on units of work called parcels that move through user-definable lifecycles. It is a core
plus modules, software engineering the first module (decision 005764b0). That module brings a
software engineering lifecycle (ideas through discovery, tasks through refine, build, review,
closed) that a new project gets with nothing configured (decision 2d171fa7). It replaces hall9k at
parity and then goes past it, because hall9k's engines become data, its store becomes one SQLite
file per node, its transport becomes one signed event stream per node, and its vocabulary is
parcel, type, lifecycle, stage, flow, flow run, agent, agent step, interactive step, human step,
system step, trigger, paused, and module (decision 078429dc); naming.md is where the full
vocabulary is settled.

## 2. What it is, and what it refuses

It is a work system. hall9k's scope line "not a task manager" is overturned: Coanda holds the
record of work and is the source of truth; Jira, Linear, and GitHub issues are optional
integrations, each packaged by a module (section 14.4), fed from it and feeding into it.

It refuses to be a programming language. Definitions route; they do not compute (decision
fa75e412). Users customize types, flows, agents, and triggers as validated data the daemon loads,
never code the daemon runs, and a domain extends Coanda as a module (decision 005764b0). A need
beyond routing is an agent step or a system step, in the core or in a module, never an expression
in a definition (decision fa75e412): an agent step where it needs reasoning, a system step where it
needs a deterministic effect.

It refuses to make a new user configure anything. The shipped lifecycle is the product, not a
template; customization is a deliberate separate act, absent from the board, the default CLI
output, and the first run. The measure is cold install to first merged pull request, and it must
not grow as the system gains features.

It refuses to depend on git. Git is the default carrier for the ledger, which is a seam (section
6), and the core knows a repo only as a named git remote that carries the ledger; branches,
worktrees, pull requests, and the code host are the software engineering module's (decision
269e586e).

## 3. What is kept from hall9k

Everything hall9k learned carries over; what changes is where it lives and how it is expressed.
Kept as kernel behaviour: hash arbitration for claims, the generation fence (local to a node),
adopt-on-restart, pause with a lever, two-lens fresh-context review, the decisions and lessons
record, two-tier identity with three replication scopes, vouches, revocation, and succession by
ranked keys. Kept in the software engineering module rather than the kernel (section 14.10):
worktree per build, and the pre-approved gated merge with the head-commit guard.
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
   core system steps.
3. Definitions: parcel types, flows, agents, and triggers, as aggregates in the store, with scopes
   and forks (decision fa75e412).
4. The interpreter: a pure function from a flow, the current position, and the last
   answer to the next move.
5. The executor seam: one interface to vendor agent runtimes, with a Claude Code adapter and a
   scripted test adapter.
6. The transport: signed per-node JSONL segments on a ledger home that is a seam.
7. The surfaces: window, CLI, rendered files, portal, MCP, all renderings of one registry.
8. Modules: out-of-process programs the daemon supervises, the software engineering module first
   (decision 005764b0).

## 5. The store

One SQLite file per node in the node home. Marten's API through Fisher (JasperFx, MIT, Marten on
SQLite; v1.27.0 as of 2026-11-02, releases several times a week, formally supported). Events are
the truth; projections produce every view, including the tables a portal or desktop surface reads.
Project is a tenant key on every stream (conjoined tenancy), so a project is a filter, not a file;
node-level records (the node's own identity file aside, nodes, credential profiles, fleet state) live
beside project streams. The identity records that decide trust (owner root, vouches, revocations,
members, succession) are not node-level: they are recorded in each project's tenant and travel with
the project (section 6), because export carries project tenants only.

The daemon is the only writer. The CLI, the portal, MCP, and the relay call one loopback command
and query API on the daemon; the CLI starts the daemon when it is not running. This removes the
second-writer problem Fisher's guidance warns about and removes store and code generation from the
CLI, which becomes thin.

Conditions carried from the Fisher evaluation: pin the Fisher version; async projections carry
version constants; write ceremony (optimistic concurrency) only on the parcel, the flow run,
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

Partial access inside a multi-repo project: every event carries the project's repos that carry
it, a tag a module-owned field fills, or "project-wide" for membership, definitions, and decisions
(decision f11d3247). The software engineering module fills the tag from the task type's
module-owned repos field, and the core never computes it. Each repo's ref namespace carries the
project-wide events plus the events whose tag names that repo; a node pushes to and imports from
every repo of the project it holds, and the import's idempotency on origin plus sequence stores an
event that arrives through two repos once (decision 1c6c858b). A teammate who holds one repo sees
the project's membership, definitions, and decisions, and that repo's parcels, nothing else. The
tag is filled in phase 3; multi-repo push and import arrive with multi-repo projects.

A project's repos follow section 14.9. The rule: a node receives the events the repos it holds
carry (decision f11d3247). The guidance (Brian, 2026-11-04): every member of a multi-repo project
holds every repo, even one they do not work in, unless they want none of its events. The intent,
deferred to multi-repo push and import (decision f11d3247): forwarding, where a node holding
several repos pushes into each the segments of other origins that repo lacks and whose tag admits,
as the same signed bytes under the origin's own ref, with no envelope, the chain intact, and import
unchanged. With it come a retag event, one event the author pushes to both the old and the new repo
sets when a module narrows a parcel's repos, so a node losing sight of the parcel learns it moved
rather than seeing it freeze, and an import that tolerates a parcel stream a node holds a prefix of,
with surfaces showing the parcel as continuing in a repository this node does not hold. A dumb
pass-through relay cannot give partial access without reading the tag or per-repo-set encryption;
that is a later reopening (section 14.9).

Volume, from hall9k's own record: 62 MB raw over about 80 days across two nodes; about 12 MB per
node per month before filtering, 2 to 3 MB packed; 25 to 35 MB per node per year in the repo.

The ledger home is a seam whose value is a replicated project setting. Its default kind is the
project's repos, all of them, each carrying the ledger in its ref namespace (decision 1c6c858b);
the relay is a later kind, and when it exists a project's requirement of at least one repo lifts
(section 14.9). Because every node can regenerate its segments for any destination from its own
table, a hosting repo is a mailbox, not an archive: a move command pushes to the new home, writes
the new location, and leaves a forwarding ref in the old one. Runbook rule: move before you
archive.

Trust lives on the event, not the carrier. Each exported event carries origin, sequence, the hash
of the previous event from that origin, and an ed25519 signature over those plus the payload (a
hash chain per node). A reader verifies any prefix and detects a rewind or fork by the chain
breaking. Trust is recomputed on import against the members and succession records. Git signing was
considered and withdrawn because the relay has no commit to sign.

The line. One chain and one contiguous sequence run per origin per project: an origin's sequence in
a project starts at 1 and steps by one, and its first line in that project names no previous line.
A line is one JSON object on one line, UTF-8, with these members in this order and nothing else:

- `format`: the line format's version, the integer 1. A later format (a skip link, for instance)
  takes the next number and is read beside this one; a reader refuses a version it does not know.
- `project`: the project's id as its registration records it (`ProjectId`), not its name, since
  names can repeat across owners.
- `origin`: the origin's node id.
- `sequence`: the origin's sequence in that project, an integer from 1.
- `previous`: the hash of the origin's previous line in that project, or `null` for its first line.
- `tag`: an array of the names of the repos that carry the event, in ordinal order with none
  repeated, or the string `"project-wide"`, which no array, an empty one included, can equal.
- `stream`: the event's stream id.
- `type`: the event's type name as the events table stores it (for example `definition_drafted`).
- `data`: the event's data, exactly the JSON text the events table stores with only the whitespace
  outside its strings removed, so every number keeps its digits and every string its characters and
  escapes.
- `signer`: the signer's ed25519 public key, 32 bytes in standard base64 with padding.
- `signature`: the ed25519 signature, 64 bytes in standard base64 with padding.

Ids are lowercase hyphenated UUIDs, integers are plain decimal, and the strings outside `data` are
escaped as RFC 8785 escapes them (a quote, a backslash, and the control characters, nothing else).
The signature covers every member but itself: it is over the UTF-8 bytes of the line with its
signature member removed, that is, the line up to the comma before `"signature"`, closed with `}`. A
line has one way to be written, and a line written any other way does not parse, so its bytes are a
function of what it says. The line does not use the JSON Canonicalization Scheme the module approval
uses, because that scheme writes numbers through IEEE doubles and event data can hold any number
(9007199254740993 and 9007199254740992 would canonicalize alike); no event is refused for the size or
precision of a number. A line's hash is `sha256:` and the full SHA-256 digest, in lowercase hex and
never truncated, of the whole line as written, signature included, without its line ending.

A node's identity is its node id, a UUIDv7, and its ed25519 key pair, made at the daemon's first start
on a home and kept in the home's owner-only `node-identity.json`, never rewritten. A home holding part
of one, or one that does not parse, is refused at start rather than given a second identity.

Identity: one owner root, one key per node, vouches, root-only revocation, and succession by ranked
keys (successor named, root rotated, successor revoked), carried over from hall9k as events. The
identity events (owner root recorded, node vouched, node revoked, member added, member removed,
successor named, root rotated, successor revoked) are recorded in each project's tenant, never the
node tenant, and travel with the project in its export, so every node holding the project reads the
same records. Trust is one pure function in the core over those records, each with its signer key,
origin, and sequence, and the key the reader trusts as root: it answers whether a key may sign for an
origin and why not, the ranked root set, and a member key's role, and it reads the records as a set,
so any arrival order gives the same answers. A record counts only when its signer may sign for the
record's own origin. An offline heir key is a later option for the single-node owner.

hall9k settled "latest wins", "an earlier key outranks a later one", and "the first rotation to land
wins" by the order of one shared ledger. Coanda has no shared order, only one chain per origin per
project, so this version changes those inherited rules into forms that need none:

- A node id and a key are bound one to one. The root record binds the root node to the root key and a
  member added binds the member's root node to its root key, so a vouch pairing either side with
  another does not count; when counting vouches pair one node id with two keys, or one key with two
  node ids, none of those pairings may sign. Because of the binding, every record a key signs sits in
  one chain, which is what makes "earlier in a chain" well defined.
- A revoked key stays revoked in this version, and every record it signed stops counting, whenever it
  was signed. The recovery from a mistaken revocation is a fresh node through join.
- Rank follows the rotation chain from the original root: the original root ranks 0, and a counting
  rotation, signed by a successor its superseded key named, ranks the successor one below the key it
  supersedes. The rank of a key is settled by the keys that outrank it alone, since a revocation of a
  key that outranks its signer, or of the signer itself, is ignored.
- Revoking a successor voids every root-set act its key signed (revocations, namings, successor
  revocations, member changes, and its rotation), whenever made. The vouch that admitted its node,
  and the vouches its key signed, stand unless a node revocation revokes its key.
- Competing rotations of one key settle by the order in which that key named their successors in its
  own chain; a voided rotation takes no part. A node that acted on the losing rotation changes its
  answers when the winner arrives.
- A member removal stands in this version, whatever adds are recorded. A member's role is the role of
  the add signed by the highest-ranked key, and among that key's adds the latest in its chain; the
  role has no effect on signing, and no member joins the ranked root set.

The relay, when it exists, is a dumb multi-tenant pass-through with no retention. Whether it is
pure pass-through or an encrypted store-and-forward buffer it cannot read is still open.

There are no locks or leases. Contention is settled by hash arbitration; the publish-and-fetch seam
and the generation fence stay local to a node.

## 7. Flows, flow runs, triggers, and the interpreter

A flow is a sequence of steps, and a flow run is one execution of a flow on a parcel: the flow run
follows the flow the way a Coanda flow follows the curve of a surface. An agent step names an
agent and an answer schema; the daemon spawns the agent and validates its answer. A system step
is a deterministic effect with no agent, run by the daemon or dispatched by the daemon to the module
that brings it, one step at a time and never a whole flow (decision 253e33e2). The core's own set is
domain-free: wait for a human, wait for an external event, timer, emit outcome, move a parcel,
derive parcels. The software engineering module brings worktree, gates, open PR, gated merge, and
rebase (section 14.10). Each step has transitions: a table of answer field equals value,
visited fewer than N times, go to step, else pause. Caps are mandatory per transition and per flow
(budget, spawns); the daemon refuses to load a flow without them. hall9k's task run is one
flow: build, three reviews, fix, two reviews, fix, until clean or capped. Its plan run is a
second. Idea discovery is a third. A flow may be one step. A flow run pauses when it waits for a
person or an external event, or when a cap or its budget is reached; a paused flow run is live and is
resumed or stopped, and a stopped flow run is final.

A trigger is a definition, drafted, published, scoped, and forked like the others, and a module
may ship one (decision d97f5f86). It is an event subscription: an event kind plus equality filters,
data that routes and never computes (decision fa75e412). A parcel entering a stage on this node, a
schedule tick, a manual start, another flow run's typed outcome, or an external event a module
reports, such as a PR assigned to a person. A trigger starts a flow run of the flow it names, on a
new parcel of a type it names or on the parcel the event names (section 14.4). An external event
carries an external id, so recording it is idempotent on project, kind, and external id, and the
core compiles the triggers on a module's event kinds into the watch requests it
hands that module (decision d97f5f86). A matching event also resumes a step waiting for it (section
14.12). A person may own a trigger (a subscription), which is what hall9k's personas become. If
matching needs judgment, the flow starts with an agent step that may answer "not for me".

Flows compose: a flow declares a typed outcome (PR opened, report produced, parcels
derived), and the next flow follows it, so a flow run ending with that outcome starts a flow run of the
next flow on the same parcel, with a depth cap and a budget the chain shares.

The interpreter is a pure function: given the flow, the position, and the answer just
returned, it returns the next move (go to, pause, end). Every flow run records the content hash of its
flow and of each agent it ran, so "did the new review flow help" is a query.

Brian ruled on these words on 2026-11-03 and 2026-11-04: a flow is the stored definition, and a
flow run is one execution of a flow on a parcel, always the two words; a flow run pauses and is
resumed or stopped; an agent runs on a model spec. naming.md records the words each ruling
replaced, and a bare "run" stays retired.

## 8. Parcels and types

A parcel is the unit of work a flow run acts on and the place its stage, record, and board row live; a
pause and its lever live on the parcel's flow run, and the paused stage of the lifecycle is a person's
pause of the parcel itself. A parcel has exactly one type and one stage. A parcel type is a
definition declaring the lifecycle (stages, allowed moves, entry stages, a paused stage reachable
from any live stage, exactly one terminal stage with a resolution set), the fields, and the
bindings: a gate before a transition and a flow after it. Those two extension points are the
whole middleware story.

The kernel fixes the meta-model and nothing below it: identity and provenance, claim with hash
arbitration, placement on a node, budget, and generic relations (derived-from, blocked-by,
stacked-on, member-of). An epic is member-of with a name. A type declares which types it may
derive. Relations stay within a project for now; crossing projects waits until a real
dependency between separately owned projects appears.

The software engineering module ships two types (section 14.10). Idea, with a discovery lifecycle: open, discovery, paused, closed
(resolution split, concluded, or archived). Task, with a build lifecycle: refine, build, review,
paused, closed (resolution completed, abandoned, superseded, duplicate). Discovery either carries
the idea forward or derives task parcels and closes the idea as split; each derived task starts at
refine, as does a task created directly. Fields are gated: an objective and criteria to leave
refine, a PR to leave build. The build flow ends when its pull request opens: it opens the PR,
moves the task to review, and ends with the outcome pr-opened. The merge is its own flow that
follows, performing the gated merge with the head-commit guard and closing the task as completed as
its ending act. QA is a flow that follows the build flow's pr-opened outcome on the same
parcel, with its own depth cap and the chain's budget, and the merge flow follows QA's passing
outcome where a project runs QA, or pr-opened where it does not, so the PR waits on QA by chaining.
A flow bound to review starts only when a person moves the parcel there, never when the build
flow run's own step does, because a bound entry on a parcel with a live flow run starts nothing and is never
saved for later. A parcel with a live flow run, running or paused, is closed by nobody and moved only
by that flow run's own steps; a person stops the flow run (`coa flow-run stop`) before moving or closing the
parcel. The words idea and task survive as type names; nobody relearns hall9k's vocabulary. The
task type also carries a repos field the module owns and sets outside any dispatch (decision
09a3372e), and that field is what fills the event tag of section 6 (decision f11d3247). Nothing
software-specific (branch, worktree, the build-and-test gates step, PR) leaks into the meta-model or
the interpreter; those are fields of the software engineering module's types and its system steps
(decision 269e586e).

hall9k's task types map by one rule: a new parcel type when the lifecycle or the ending differs
(review, spike), a new flow when only the work differs (content), a field when only the prompt
varies.

Type versioning: a parcel records the content hash of its type at creation; a new type version
applies to new parcels; an explicit migrate command moves an existing parcel only when its current
stage exists by name in the new version.

## 9. Definitions, scopes, and forks

Parcel types, flows, agents, and triggers are aggregates in the store (decision fa75e412), created
and changed by commands with a role-checked principal, replicated like any record. Files appear only
for export, import, and an editing round-trip. Decisive reason: a project outside software has no
code repo to hold files.

Definitions carry a kernel-hardwired status: draft, published, retired. Only published governs
work; publishing needs an owner-role principal. This is how an agent proposes a flow change (a
draft) without it taking effect until a person publishes it. It is not a parcel lifecycle and is not
called one.

Three scopes, nearest wins by name: project (shared with teammates through the project ledger),
owner (fleet-replicated to every node and project the owner holds), and module (imported from an
installed module at install, once per node, read-only, versioned with the module; decision
836ec405). A customization of a module's definition is a fork that records its base: the definition's
name, the module name and module version, and the base's content hash. The content hash, with the
name and module, is what identifies the base on another node; the module version is recorded for
the reader but not matched. One module version can hold more than one content of a definition, and
a node records an unchanged content under the first module version it imported it at, so two nodes
can hold the same base under different module versions. Unforked users take new module versions automatically; forked
users get a notice and a three-way diff command, with fold-in an explicit act. Nothing is ever
silently replaced. A shareable template is an export of a set of definitions; import recreates
them.

An agent definition holds a persona prompt (role, purpose, output, rules, prohibitions), a model
spec (vendor, model, effort), a tool set, skills by name, MCP servers, and the answer schema.
Per-parcel context (criteria, journal, notes, code) is assembled by the daemon per step from
declared sources and never folded into the agent. Skills stay vendor-native repo files referenced
by name. Credentials are per node, never in the store.

Text from an external system is untrusted: the data an MCP server named on an agent definition
returns, and the fields of an event a module reports (decision d97f5f86). It is read only in an
agent step with a narrow tool set and is never allowed to steer a step with write power. A module
confines credentials; it does not make content safer (section 14.4).

## 10. The executor seam

One interface: spawn, collect, resume, send into a live session, stop, list, reattach after a daemon
restart. An adapter per vendor renders an agent definition into the vendor's invocation: persona to
system prompt, tool set to allowlist plus deny hook, MCP servers to the vendor's config, skills to
tier paths, model spec to flags; caps passed natively where the vendor has them, with the daemon's
timer authoritative. Every agent step ends in one JSON document validated against the step's schema;
invalid or missing is retried once by resuming the same session, then the flow run pauses with the
transcript as the lever. The parcel keeps its stage; its live flow run is where the pause and the lever
are found, and a resume performs the step again with a new spawn. hall9k's fifteen marker-line
grammars do not exist.

Spawn takes a credential profile. A node holds named profiles per vendor; a project carries a
node-local, never-replicated setting naming its profile; a binding may override per agent. For
Claude Code a profile is a config directory (subscription login, via `CLAUDE_CONFIG_DIR` on the
spawned process, documented for multiple accounts) or an API key helper. Subscription profiles
cannot use bare mode; the Agent SDK may not use subscription logins at all, which confirms the CLI
adapter as primary.

Measurement on every spawn, recorded on the parcel's flow run, in the agent step's answer event and in
the pause event: flow hash, agent hash, model spec, tokens, cost, turns, duration, answer. A
agent step whose answer or pause is not recorded still records what it spent, in a usage record that
counts toward the flow run's cost and changes nothing else: one a person's pause or stop ended, which
stops its session at once, and one whose answer came from a session its visit no longer expects,
because a resume came after its launch. Its usage is every attempt the executor returned plus what
stopping the session reported for the attempt in progress.

Two adapters in the first version: Claude Code headless, and a scripted adapter returning canned
answers so flows and the interpreter are tested without a vendor or a network.

## 11. Surfaces

One registry of commands, queries, and actions, each entry carrying a name, a kind, a description,
and the roles allowed to call it. Every surface is a rendering of it, role-scoped.

The window: an interactive session in the project home holding the owner principal, the operator's
primary surface. It reads, rules, and starts; it never drives a lifecycle a flow run is driving, and it
spawns nothing itself (discovery, challenge, walks are flows whose flow runs the daemon runs). Its
recipe is a stored agent definition: a hand edit is a draft and a publish, a regeneration is a draft
to diff. The courier is a trigger on attention events bound to a deliver-to-window system step,
pushing into the window's live session through the vendor's inbound channel, with the batching wait
and the bypass list as binding parameters.

The CLI, `coa`: noun-first (parcel, flow, flow run, agent, project, node, and module, with
`coa module` for a node's modules and `coa project module` for a project's), thin, calls the daemon
and starts it when needed; verbs follow the registry. Sessions talk back through it, with their
principal in a token the adapter places in the environment. A core verb may take the settings of
the node's default modules as a convenience: `coa project add` takes the software module's branch,
build, and test settings, so registering a project is one command. Otherwise a module's settings are
set through generic module commands rendered from the registry, never through module-specific verbs
(section 14.6).

Rendered files (task.md, prompt.md, decisions.md, lessons.md) are projections written at spawn,
never truth, never edited.

The portal: a local page served by the daemon on the loopback, for attention and walks first, not
a kanban; reachable through the relay later. MCP: the registry over loopback streamable HTTP with a
stdio shim, last.

## 12. Integrations

Integrations are what a module packages; section 14 is their design. GitHub, Jira, and Linear each
reach Coanda through a module, in both directions: inbound, the module reports external events
(issue assigned, PR assigned) under the watch requests the core compiles from the user's triggers
(decision d97f5f86); outbound, it mirrors status as a feed reaction (decision ad77d993). A tracker's
stage-to-status mapping is one of the module's replicated per-project settings (decision 30327d72),
not a field of the parcel type. Coanda is the truth in both directions.

The pre-approved merge is Coanda's own, the software engineering module's gated-merge step (section
14.10): inspect the PR, then merge with the head-commit guard
and the skew check. GitHub's auto-merge feature is not used, because it would merge a head pushed
after the last inspection.

## 13. Operations

One self-contained binary per platform, the daemon and the CLI in one executable, carried in the
release archive with the software engineering module's artifact beside it. Install is download and
run; the SQLite file is created on first start in the node home, and the first daemon start
installs the software engineering module from the release archive (decision 836ec405). The
measure of section 2, cold install to first merged pull request, includes that module install
(section 14.13). Update fetches the next GitHub release and restarts when no step is in progress,
reattaching live sessions. Doctor checks git, the agent CLI, and the node key, and checks each
installed module: present, approved, healthy, and within the supported module API range (section
14.7); gh is the software engineering module's tool, not the core's. The daemon runs as a user
process started on demand; service registration is later. Windows through the same process-manager
seam. One local-time daemon log per node, and beside it one log per module process (section 14.7),
plus the measurement events, which are what gets queried. The node
home's token file (daemon.json), its database with the files SQLite keeps beside it, its log,
the file a detached daemon prints to outside its log (daemon.out), and the node identity with its
private key (node-identity.json) are readable and writable
by their owner alone from the moment each is created: mode 0600 on macOS and Linux, and on Windows the node home carries an owner-only access list, not inherited from its
parent, that every file created inside it inherits. A start narrows any of them an earlier run left
wider before it opens the database.

## 14. Core and modules

### 14.1 What a module is

Coanda is a core plus modules (decision 005764b0). The core is the work system: parcels, types with
their lifecycles, flows and flow runs, agents, triggers, the domain-free system steps, the executor
seam, the store, the transport, identity, and the surfaces. Users customize it with data: types,
flows, agents, and triggers, each a validated definition. A module extends it with code: an
out-of-process program the daemon installs, starts, and supervises, speaking a JSON contract in both
directions, writable in any language, loaded only when a signed approval the node accepts covers the
exact artifact on disk.

A module is not a new architecture. It packages three seams the design already had (integration
adapters behind one interface, definitions as import and export, and the system step registry) with
a process boundary, a trust record, and two new directions of information (watch requests down to
the module, an event feed down to the module). The one thing a third party could not do before and
can do after is supply a system step.

A module brings system steps, and the core composes them into flows and runs them. Dispatch is per
step, never per flow. A module never owns a flow run, never starts one, and never decides what work
follows an event; those belong to flows and triggers, which are the user's data. This is the line
that keeps a module from becoming one of hall9k's engines one process away (decision fa75e412's
origin), and it is the most important sentence in this section.

Software engineering is the first module. It is not part of the Coanda binary: it is a separate
artifact carried in the same release archive, installed by the first daemon start, and switched on
for every new project by default, so a new project still configures nothing (decision 2d171fa7).
Pulling it out of the archive later means only that it stops being bundled.

### 14.2 What a module brings

- System steps, each with a name, an argument schema, and a result schema. A step's result carries a
  verdict the flow's routes read, as every system step's does today.
- Definitions: types with their lifecycles, flows, agents, and triggers, imported into the store in
  a module scope at install (the module scope of section 9, versioned with the module). They have no
  privilege a user's definitions lack; a user forks one and the fork records its base, so a new
  module version raises a notice and a three-way diff rather than a silent replacement.
- External event kinds it can report, with the fields a watch may filter on, and the core event
  kinds it subscribes to.
- A settings schema for its per-project settings, and named credential profiles held on the node.
- Fields it owns on the types it ships, which it may set outside any dispatch (14.5).

Describe is capability-based: it lists steps, event kinds with their watchable fields, core event
subscriptions, the settings schema, and the module API version. A later capability, such as an
executor, slots in as another list without reopening the contract. A module brings steps today and
may bring other capabilities later; steps are not the only verb.

### 14.3 The contract, core to module

Four calls on the module, over HTTP and JSON on the loopback, with the module's address and a token
in the environment the way an agent step's session gets its own (`SessionAccess`):

- **health**: the daemon waits on it after starting the process.
- **describe**: asked at install and again after every start; the running module's answer is what
  flows are validated against and what the daemon routes by.
- **dispatch**: one system step. The module receives ids and the step's arguments only: the project,
  the parcel id, the flow run id, the step's name, the visit number, a dispatch id the daemon minted
  and recorded on the flow run before calling, the resumed flag wait steps read today, the flow
  run's work path, and the step's arguments as the JSON object the flow author wrote. Everything
  else (the parcel's fields, earlier steps' results, the project's repos, the module's own settings)
  the module pulls through the core's query API under its Module role. A dispatch never carries the
  flow definition. The module reads flow runs under its Module role as section 14.5's wide reads
  allow, and its read of flows gives a flow's name, version, and content hash but never its steps.
  It answers **done** with a result document validated against the step's declared result schema,
  **paused** with a reason and a lever, or **running**. A module that errors, times out, or answers
  something the schema refuses pauses the flow run with the dispatch-again lever, as a thrown
  exception does today.
- **status** and **stop** on a dispatch. A running dispatch later records its outcome through a core
  command naming the dispatch id. After a daemon restart the daemon asks status instead of
  dispatching again, which is the executor seam's reattach applied to system steps; on unknown it
  dispatches again under the same id. A person's stop calls stop, and the daemon records nothing, as
  today. Status may carry free-text progress, shown to a person and never routed on.

Dispatching a step again under the same dispatch id must be safe, and the module, not the daemon,
guarantees it, because only the module can check the world. This is already practice in the software
steps: open-pr reuses the open pull request whose head it already is, gated-merge checks the
recorded head and an already-merged pull request, and gates re-run. The contract states it as an
obligation; the dispatch id is the daemon's help, and a module that lost its memory re-derives the
state from the world.

The agent step's working directory is the core's. A dispatch hands the module the flow run's work
path (`NodeHome.FlowRunWorkPath`), and a step that prepares a workspace populates that path, as the
worktree step does today; the core keeps writing its rendered files under `.coanda/` there, and the
deny hook's carve-out is unchanged.

### 14.4 The contract, module to core

The module calls the daemon's existing loopback command and query API, presenting a module token,
under a Module role (decision c4d0586a: every call carries a principal). The same token middleware
resolves three kinds of token: the daemon token (owner), a session token bound to one agent step's
launch, and a module token bound to one installed module.

**Events.** A module reports external events (review requested on a pull request, mail arrived from
this sender) by recording them, with the kind prefixed by the module's name and an external id, so
recording is idempotent on project, kind, and external id. What follows is the user's trigger: it
creates a parcel of a type, or targets the parcel the event names, and starts a flow. The module
identifies (its reads are wide, so it may look up the parcel for pull request 12 and put its id in
the event); it never decides. When deciding takes judgment, the trigger starts a flow whose first
step is an agent that may answer "not for me" (section 7).

**Watch requests.** An external event is a project-scope record that replicates to every teammate's
node, so a module must not report everything it sees. The core compiles the user's triggers (event
kind plus equality filters, the user's data, shippable by a module) into watch requests and hands
them to the module at start and whenever a trigger changes; describe declares, per event kind, the
fields a watch may filter on. The filter runs inside the module and it is the user's filter.

**Untrusted text.** The fields of an event a module records are untrusted text under the rule
section 9 states: read only in an agent step with a narrow tool set, never allowed to steer a step
with write power. A module confines credentials; it does not make content safer.

**The feed.** A module cannot otherwise learn what Coanda itself did, and three modules already
named need to: an outbound status mirror to Jira or Linear, worktree release once worktrees are the
module's, and a notifier. Expressing these as flows fails on an existing rule: the trigger loop
refuses a bound entry made while the parcel had a live flow run and never saves it for later
(`TriggerLoop.EnteredAsync`), and the build flow's own move-to-review step is such an entry. So
describe declares the core event kinds the module subscribes to (parcel entered a stage, flow run
ended, stopped, paused, field set), and the core delivers them in sequence order with a per-module
position kept in the node tenant, as the trigger loop keeps its own, at least once, under the
module's idempotency obligation, only from projects the module is switched on for and narrowed by
its per-project settings. The rule of thumb: if a person should see the effect in the parcel's
history, it is a step the daemon dispatches; a mirror of Coanda elsewhere or a message about the
work is a feed reaction. Work goes in flows; the feed is for observing and mirroring. The feed lets
something happen in the world that no flow run records, and only this rule and review stand between
it and modules that do work beside flows; the design says so.

### 14.5 The Module role

The Module role is a correctness boundary, not a security layer. A module runs as the same OS user
as the daemon: it can read `daemon.json` and present the daemon token, read the SQLite file, run `gh
auth token`, and read the Claude Code configuration. Against a malicious module the role is nothing.
Against an honest module that is steered, by a crafted pull request body or a mail it reported, the
role and the narrow step argument schemas are what keep the damage inside the step it was
dispatched. The security layers are the signature gate (14.8) and, later, OS isolation: a separate
user per module or a container, which nothing here prevents.

What the role allows:

- **Reads, wide.** Parcels, flow runs, types, flows, agents, triggers, the project and its repos,
  and its own settings, in any project the module is switched on for.
- **Events.** Record external events with module-prefixed kinds in those projects.
- **Writes during a dispatch, narrow.** A step result, and set-field on the parcel, only on the flow
  run and parcel of a dispatch the module currently holds, checked by dispatch id, as a session is
  scoped to its launch today.
- **Module-owned fields.** A type a module ships may declare fields the module owns; the module may
  set those on any parcel in a project it is switched on for, with no dispatch. This is how inbound
  sync (a Jira title changes, the parcel's field follows) and the software task type's repos field
  work. The repos field is what the core reads to fill the replication tag (14.9).
- **Its own settings**, set and shown, in two scopes (14.6).

What it refuses: parcel add, move, and close; flow run start, resume, and stop; definition publish;
project registration; daemon stop. Moving and closing stay core steps the flow author places, as the
shipped flows already do.

### 14.6 Module settings and credentials

A module's per-project settings split into two scopes, on section 10's pattern. Replicated settings
carry what the project agrees on, with the schema describe declares: the primary branch per repo,
the build and test commands, the required checks, a stage-to-status mapping, a channel. Node-local
settings carry what this node supplies: the credential profile and other node resources. The node
holds named profiles per module; the secret material lives in the module's owner-only area, and only
the profile names and each project's selection live in the core. Every dispatch, watch request, and
feed delivery carries the project, so the module resolves the project's profile.

A setting may declare a default, and a credential profile's default may be the node's default
identity: the software module's GitHub profile defaults to the gh CLI's default account. A module
switched on for a project is unconfigured on this node when, and only when, a required setting has
no value there: a replicated setting with no value and no default, or a credential setting with no
selected profile and no default. A profile selected for the project that this node no longer holds
counts as unconfigured, and the default does not stand in for it, because an explicit choice of
identity is never silently replaced by another. While a module is unconfigured, its steps pause with
a lever naming the setting, it issues no watch requests, and it skips feed reactions with a log
line.

Part of that is built. The feed skips a delivery to a module that is unconfigured for the event's
project, and logs it. The daemon does not yet hold back a dispatch to such a module. Of the software
module's steps, only open-pr and gated-merge read the credential profile, and they pause with the
lever naming that setting when the profile selected for the project yields no GitHub token or is
one this node no longer holds.

### 14.7 Supervision

A module's manifest names its start command. The daemon starts the process with its address and
module token in the environment, waits for health, calls describe, and records the answer. It
restarts a failed process with backoff, and when a module keeps failing it pauses the flow runs that
reach the module's steps, naming the module in the reason; it never dispatches a step to a module
that is off or unhealthy, and the pause is the honest state. Each process writes its own log under
the node home, beside the daemon's. Doctor checks installed modules: present, approved, healthy, and
within the supported API range. A version change is noticed at describe after the start, before any
flow run reaches a step that moved. A project runs one version of a module at a time.

### 14.8 Install, approval, and trust

**The artifact** is a directory with `module.json` (name, a plain directory name that prefixes its
steps and event kinds; version; module API version; start command; source URL and commit), the
executable, and a definitions folder of JSON. The manifest does not list steps; describe is the one
source of truth about what the module does.

**Install** is per node and not replicated: verify the approval against the artifact's hash; unpack
to `<node home>/modules/<name>/<version>/`; start, health, describe; import the definitions in
module scope; issue the module token; record the install (version, hash, approval, describe
snapshot) in the node tenant. Update unpacks the new version beside the old, runs the same sequence,
switches, and removes the old once the switch is recorded. **Switch-on** is a replicated project
setting, the set of modules the project uses, so a teammate's node learns from the ledger which
modules the project wants; one it lacks shows as flow runs pausing and naming it. Registering a
project switches on the modules the node configuration marks default, the software module by
default.

**Approval** is one signed record: module, version, artifact hash, module API version, source URL
and commit, signer, issued at, and a review id; signed with ed25519, the primitive the event chain
uses. The daemon checks it at install and at every start of the module against the hash of what is
on disk. Two signers are accepted through one verification path: the registry key shipped with the
binary, and the node owner's root key (section 6). Every module version is reviewed again. The
registry's signatures come from an automated review: the module's source must be open or
source-available, and a reviewing agent with read-only tools, treating the module's text as
untrusted (prompt injection aimed at the reviewer), scans it for vulnerabilities and above all for
malicious capability. The review pipeline is built later; the review id is its hook. Until it exists
the registry's approved list holds one entry, the software engineering module.

**Owner-signed modules.** An owner may have their agent write a module and sign it with `coa module
sign`, which produces an approval the install verifies like any other. Its record reads "approved by
owner, not reviewed" wherever modules are listed. This is how a user who is not a software engineer
extends Coanda, and how a module author runs a module while developing it; its security story is
exactly that of running an agent with full permissions on one's own machine, and the label keeps
that visible. On a team member's node an owner-signed module requires an explicit accept the first
time it is switched on for a project that node holds; membership trust covers the owner's data, not
running unreviewed code as a supervised process. The registry path stays automatic. A member cannot
sign.

**Revocation.** The registry publishes signed revocations; the daemon fetches them at install, at
update, and at start when its copy is older than a few days, and a stale copy still starts installed
modules and logs that it is stale. A revoked module is stopped, and flow runs that need its steps
pause naming the module and the reason. The only override is the owner re-signing that exact
artifact as owner-approved, which the record shows. An owner withdraws their own signature by
recording a revocation event, and their team's nodes stop the module.

**Sharing.** The ledger carries a module's identity, never its bytes (section 6: large artifacts are
referenced, never events). The switch-on record names module, version, hash, and approval; a node
fetches the artifact by that record, from the registry for a registry-approved module or from a
module ref in the project's own repos (`refs/coanda/modules/<name>/<version>`) for an owner-signed
one, and refuses an artifact whose hash does not match. Offline, `coa module export` writes the
artifact directory as a module archive and `coa module install <file>` reads one. "Package" is not a
Coanda word.

**Versioning.** An integer module API version in the manifest and in describe; the daemon supports a
range and refuses to install or start a module outside it, naming both. Module and Coanda versions
are otherwise independent. A fork's base is the definition's name, module name, module version,
and content hash, and another node finds it by the name, module, and content hash alone: one module
version can hold more than one content of a definition, and a node records an unchanged content
under the first module version it imported it at (section 9).

**Multiple modules.** A project's switched-on set may hold several. Step names and event kinds are
module-prefixed (`software/gates`, `cms/publish`); definition names are not, and install refuses a
module whose definition name collides with another installed module's in module scope. Each module
has its own settings, workspace, token, and profiles. Several versions of a module may be installed
on one node, so two projects may run different versions.

### 14.9 Repos and replication

The core knows a repo as a named git remote it clones, fetches, and pushes refs to, kept under the
project; it knows no branch, worktree, pull request, or GitHub. Those are the software engineering
module's (decision 269e586e). The git binary is the core's carrier for the ledger and is not a
software concept.

Each project has a set of repos, at least one today because the ledger rides on git, and no primary.
The node home gives each project a directory with two folders: `projects/<project>/repos/<repo>/`,
the core's bare clones; and `projects/<project>/modules/<module>/`, each module's per-project
workspace. Two processes writing one clone's worktree metadata rely on git's own locking, which holds
for one user on one machine and wants a test.

The core makes and holds the clones (`Coanda.Daemon/Projects/RepoClones.cs`). It makes a repo's clone
when the project is registered and, for any repo still without one, at every start: a bare
repository whose `origin` is the repo's recorded remote, made beside its path and moved there once
complete, with no contact with the remote. A clone a build before the per-repo layout kept at
`clones/<project>/repo.git` moves to its project's one repo at the first start of a build that has
the layout. The start lists the clone's worktrees before the move, and does not move a clone whose
worktrees cannot be listed. At every start, after any move, it runs `git worktree repair` from each
clone for every linked worktree the clone lists whose directory exists, so a flow run paused across
the upgrade resumes in its worktree. The repair changes nothing for a worktree whose links are sound,
and running it at every start means a start stopped between the move and the repairs, or a repair
that failed, is finished by the next start. All of this happens before any module starts and before
any live flow run is advanced. What the
core cannot place or make, it logs and leaves; it never deletes or overwrites a clone.

A module finds a repo's clone by the layout, which the SDK writes once (`RepoLayout`) and the core
reads too; it is not handed a path. The software module makes no clone: a step that finds none, or
finds something git does not recognize, pauses naming the path and the next daemon start as what puts
a clone there. It cuts a flow run's worktree from the clone at the work path the dispatch carries
(section 14.3), not in its own per-project workspace. Outside a dispatch, when the feed delivers a
flow run's end or stop and in its start sweep, it reads that flow run's work path from the core's flow
run view (`GET /flow-runs/{id}`, field `workPath`, which is present and `null` for a project whose name is not
a plain directory name). That view is the core's API, not the module wire schemas. A worktree a clone lists
counts as a flow run's only at the path the view gives that flow run, and a worktree anywhere else is
never removed.

Each clone fetches by two refspecs: the default `+refs/heads/*:refs/remotes/origin/*`, and the ledger's
`+refs/coanda/nodes/*:refs/coanda/fetched/nodes/*`. The ledger refspec brings every node's refs from
the remote into `refs/coanda/fetched/nodes/`, a namespace no other refspec writes and no ref of the
node's own sits in. It is apart from `refs/heads/`, `refs/tags/`, and `refs/remotes/`; from
`refs/coanda/nodes/`, where this node writes its own ledger refs; and from the software module's
`refs/coanda/rebase/`. A plain fetch therefore never moves a ref this node writes, and no branch on
the remote maps onto a fetched ledger ref. The refspec is forced, so a peer's rewritten ref is fetched
as it stands and import, not git, judges it.

The ledger home setting's default kind is "the project's repos, all of them"; the relay is a later
kind, and the at-least-one-repo requirement lifts when it exists, without the module system hearing
of it. Section 6's rules hold: each repo's ref namespace carries the project-wide events
(membership, definitions, decisions) plus the events whose tag names that repo; a node pushes to and
imports from every repo it holds; import is idempotent on origin plus sequence, so an event that
arrives through two repos is stored once. An archived or removed repo loses nothing project-wide,
and a node regenerates that repo's own events to any destination from its own table: move before you
archive. The tag on an event is the project's repos that carry it, and the software module fills it
from the task type's module-owned repos field; the core never computes it.

Section 6 carries this in its repos paragraph. The rule: a node receives the events the repos it
holds carry. The guidance (Brian, 2026-11-04): every member of a multi-repo project holds every
repo, even one they do not work in, unless they want none of its events. The intent, deferred to
where section 6 already places multi-repo push and import: forwarding, where a node holding several
repos pushes into each the segments of other origins that repo lacks and whose tag admits, as the
same signed bytes under the origin's own ref, with no envelope, the chain intact, and import
unchanged. With it come a retag event, one event the author pushes to both the old and the new repo
sets when a module narrows a parcel's repos, so a node losing sight of the parcel learns it moved
rather than seeing it freeze, and an import that tolerates a parcel stream a node holds a prefix of,
with surfaces showing the parcel as continuing in a repository this node does not hold. A dumb
pass-through relay cannot give partial access without reading the tag or per-repo-set encryption;
that is a later reopening.

### 14.10 The software engineering module

What moved into it, all under `src/Coanda.Modules.Software/`: the worktree, gates, open-pr,
gated-merge, and rebase steps and their recorded-heads helper, in `Steps/` (before the move they lived
in `src/Coanda.Daemon/SystemSteps/`, which now holds only the core steps); the step name list, in
`SoftwareSteps.cs` (before the move, `src/Coanda.Core/Software/SoftwareSystemSteps.cs`); the shipped
idea and task types with their lifecycles, the build, merge, discovery, and review-on-assignment flows,
and the five agents, which were C# in `src/Coanda.Core/Software/` and are now JSON in `definitions/`,
one file per type, flow, and agent; the GitHub client, in `GitHub/` (before the move,
`src/Coanda.Daemon/GitHub/`); and the worktree handling, in `Repositories/`: `WorktreeListing`,
`ProjectClones`, and `FlowRunEnds`, through which the module removes a flow run's worktree when the
module feed delivers that flow run's end or stop. `FlowRunWorkers` stays in the core, in
`src/Coanda.Daemon/FlowRuns/`, as what the module feed waits on before it delivers a flow run's end, so
no module acts on that end while a step of the flow run may still be running in its work path. The
software half of the project record, the primary branch, build and test commands, and required
checks, which `src/Coanda.Core/Projects/Project.cs` carried before the move, is now the module's
replicated settings, named in `SoftwareSettings.cs`, with its GitHub account as a node-local
credential profile. The task type gains a module-owned repos field. The shipped triggers, starting
with review requested, have not moved yet: no shipped trigger exists, and triggers as module
definitions are phase 4 work.

What stays core: the system step seam and its six core steps (move-parcel, timer, wait-for-human,
wait-for-external-event, emit-outcome, derive-parcels), whose request and outcome types become the
C# rendering of the wire contract; everything in `Executors/`, the Claude Code adapter, the tool
guard, reattach, session tokens, and credential profiles; the registry, the store, the trigger loop,
the definition service, the parcel and flow run services; the required-fields gate, which is a
lifecycle concept (a field required to leave a stage) and not the build-and-test gates step. The
command runner and the git wrapper live in the SDK, as section 14.11 says, and the daemon hands its
command runner to the module it hosts; the core will use the SDK's git wrapper when it comes to run
git for the ledger. The project record keeps its name and gains the repos list; today's single
remote is its first entry.

The definition service validates a flow's system step names against the describe answers of the
modules switched on for the project, instead of against the daemon's registered names, and the "no
implementation" pause names the module. About two thousand of the three thousand lines in the
folders named move. Decision 60c00a83 carries over: no module test touches GitHub.

### 14.11 Spec first, and the SDK

The wire contract is defined as JSON schemas in the core repository, versioned by the module API
integer: describe, dispatch, status, stop, the feed delivery, the watch request, the recorded event,
the manifest, and the approval record. The schemas are written before the wire exists. A C# module
SDK (the contract types, a client for the core API presenting the module token, the command runner
and git wrapper) lives in the solution for now and is checked against the schemas; the software
module references only the SDK, never `Coanda.Core` or `Coanda.Daemon`, and a test over its project
references says so. While the module is hosted in process, the host adapter is constrained to the
schema-defined calls, and a test says the adapter's surface is the schema's surface. One repository
per language SDK is the common pattern (Stripe, AWS, Octokit, Twilio, Anthropic, OpenTelemetry,
Temporal), and it works only when the contract is spec-first; the C# SDK moves to its own repository
when a second language appears.

### 14.12 Order against the plan (Brian's ruling, 2026-11-04 17:42 EST)

Carve now, wire in phase 5. Coanda is a first version that will replace hall9k, not a prototype, so
live data accrues from now on and a meta-model cannot be retrofitted under it (section 16); the
model changes come first and the transport waits.

1. Before phase 3 is cut: the design text and decisions (this section and the decisions it cites).
   Then the carve-out: the software module's contents move into a module project referencing only
   the SDK; the daemon hosts it in process through the constrained adapter; the Module principal,
   the dispatch id on the flow run, module-owned fields, two-scope settings with profiles, and the
   project reduced to a name plus a repos list land with it; the schemas are written and the SDK
   checked; the two tests exist. The work path, worktree release through the feed, and the
   module-owned repos field are answered in this code.
2. Phase 3 fills the event tag from the repos field, as section 6's repos paragraph describes; the
   retag event's shape is in code; forwarding is deferred to multi-repo push and import.
3. Phase 4 does the trigger-model work: triggers as definitions a module ships, a trigger that
   targets the parcel an event names, the external id with idempotent event recording, a matching
   event resuming a waiting step; the in-process module reports events through watch requests.
4. Phase 5 puts the boundary on the wire: module mode, the HTTP and JSON contract, supervision with
   backoff and pausing, the feed over the wire, install from the release archive on first run,
   verification against the registry key and the owner's root key, doctor checking modules. Its gate
   is restated: the exit scenario on Windows with the software module installed as a separate
   artifact from the release archive, started and supervised by the daemon, verified against the
   registry key.
5. After the first version, before any second module: the owner-signed path with per-node
   acceptance, module sharing through a ref, export and install from a file, the credential profile
   commands.

Rejected: wire first (about three thousand lines on the critical path against a contract that moved
three times in one day; supervision faults presenting as product faults; Windows process work done
twice), and deferring the carve-out with the wire (the tag, the triggers, and the Module principal
are model changes phases 3 and 4 need).

The two conditions that keep "later" from becoming "never": the phase 5 gate names the artifact and
the process, and the adapter is allowed no call the schemas do not define. If either is dropped, the
surviving case against this direction (the benefit accrues to domains that do not yet exist while
the cost is paid now) wins.

### 14.13 What this unlocks and what it forecloses

Unlocked: deterministic effects on the world with invariants the core need not know (merge with a
head guard today; publish once, send once tomorrow), in any language, versioned by an integer rather
than binary compatibility; an inbound seam that is a supervised process holding a cursor and a
credential, with the user's filters running inside it; an outbound mirror that does not fight the
one-live-flow-run rule; credential confinement, agents acting on a mailbox or a tracker only through
steps with narrow argument schemas; several module versions on one node; and the owner's own
modules.

Foreclosed or left to reopen: hosted or multi-tenant Coanda until modules run under OS isolation;
the single binary of section 13 becomes a binary plus an artifact, and cold install to first merged
pull request is measured with the module install in it; executors as modules are not in the
contract, though capability-based describe leaves the room; modules depending on modules need a
manifest requires line and cross-module step names, additive and undesigned; surfaces from modules
need an extensible registry, undesigned; partial access over the relay is a reopening; the
routing-only interpreter will push module authors to write small computing steps as glue, which is
the rule working as intended, and a shipped core comparison step may eventually be warranted.

### 14.14 Rejected along the way

In-process modules on the Orchard Core model (full trust, no security over out-of-process as the
same user, binary compatibility instead of wire versioning, breaks under trimming or Native AOT).
Scripting in definitions (decision fa75e412). WebAssembly (immature in .NET). MCP as the
core-to-module transport (no dispatch id, no status after reconnect, no module-to-core direction;
bending it defines the same contract in other clothes). Building the software steps in and leaving
other domains agent steps with MCP servers (covers a draft, not a deterministic effect with an
invariant). A module that owns a flow run (an engine one process away). A module filtering what it
reports through its own settings (the filter must be the user's trigger, because events replicate to
the team). Membership trust alone for owner-signed modules on a member's node. A primary repo per
project (an archived primary needs fallback logic; all repos carry). Importing a module's
definitions per project at switch-on (one copy per node is what validation and fork notices need).
Wire first.

## 15. Priorities

Do-now (the first version): store, registry, thin CLI, parcel meta-model and relations, two shipped
types, derive and split, definitions as aggregates with status and scopes and the fork-base field,
interpreter with caps, triggers, typed outcomes with depth and budget caps, executor seam with both
adapters, resume and reattach, measurement events, rendered files, signed JSONL export and import
with the hash chain, arbitration, join, identity event shapes including succession, window with
stored recipe, discovery and build and review-on-assignment flows, gated merge, operations
(binary, install, update, doctor), Windows in the last week, per-project ledger home setting,
multi-repo project fields, credential profile field. With them, in the order of section 14.12: the
carve-out of the software engineering module before phase 3, hosted in process behind the
schema-defined contract, with the Module principal, the dispatch id, module-owned fields, two-scope
settings, and the project reduced to a name plus a repos list (decision 6687d821); the event tag
from the repos field in phase 3; the trigger-model work in phase 4; and in phase 5 the module
boundary on the wire, supervision, install from the release archive, and verification against the
registry key and the owner's root key (decision 6687d821).

Fold-in (after the first version, in order): before any second module, the owner-signed path with
per-node acceptance, module sharing through a ref, module export and install from a file, and the
credential profile commands (section 14.12); then Jira and Linear, succession commands, portal,
ledger move and forwarding ref, fork notices and three-way diff, courier binding, challenge and walk
flows, export and import of definitions, type migrate command, service registration, MCP.

Deferred: cross-project relations, relay (and its encryption question), template sharing between
people, other vendors, modules under OS isolation (section 14.5), user-authored skills in the
store, business lifecycles, offline heir key.

## 16. Rejected along the way

Files in the repo as definitions (no code repo outside software; loses store versioning). A
separate ledger repository (two repos per user). One parcel type with phase-gated fields (made
split a hack). A separate lifecycle aggregate (dissolved into the parcel type). Definitions as
parcels (recursion). Git-native signing (no commit on the relay). GitHub auto-merge (no head guard).
A fixed lifecycle (Brian: building a lifecycle is a feature). Per-project SQLite files. The Agent
SDK as the primary adapter. Hardwiring idea and task in C# for the first version (a meta-model
cannot be retrofitted under live data). The portal as the first version's primary surface. A
service installer in the first version. Windows earlier than the last week.

From the module design (section 14.14 gives the reason for each): in-process modules on the Orchard
Core model; scripting in definitions (decision fa75e412); WebAssembly; MCP as the core-to-module
transport; building the software steps in and leaving other domains agent steps with MCP servers;
a module that owns a flow run (decision 253e33e2); a module filtering what it reports through its
own settings (decision d97f5f86); membership trust alone for owner-signed modules on a member's node
(decision 8dd7a358); a primary repo per project (decision 1c6c858b); importing a module's
definitions per project at switch-on (decision 836ec405); and wire first (decision 6687d821).
