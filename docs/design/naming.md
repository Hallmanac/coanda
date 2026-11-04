# Naming decisions for Coanda

Settled with Brian. The product name was settled on 2026-11-03 at 08:46 EST in the hall9k project
window, and the concept words were settled on 2026-11-03 and 2026-11-04, the last of them in the
concepts walk of 2026-11-04. This file records only names and the reasons, so no session
re-litigates them; the design itself lives in design.md.

## Product: Coanda (working name)

From the Coanda effect: a moving fluid attaches to a curved surface and follows it. The flow
follows the shape you give it, which is the builder half of the product. Checked clean on
GitHub (four trivial repos), npm, PyPI, crates.io, NuGet, and coanda.dev, coanda.sh, coanda.io
were all unregistered at check time. The ASCII spelling drops the diacritic in Coandă.

Rejected along the way, with the collision that killed each: tally (Tally Solutions ERP),
latch (Latch Labs, agent tooling), keel (keel.sh), laminar (lmnr.ai, a YC agent-observability
company), and about seventy craft, nautical, legal, and fluid words, most taken in the last year
by agent-tooling projects describing the same design. The checks are in the window's scratchpad
files name-check-round*.txt; the script is name-check.sh (GitHub exact-name repos and user/org,
four registries, three domains).

## Settled vocabulary

Concept words are deliberately plain and borrowed; only the product name is unique. These are the
words, and each means only what it says here. Where a word is settled before the thing it names is
built, its entry says so.

- parcel: A parcel is the unit of work a flow run acts on; it has exactly one type, stands at
  exactly one stage of that type's lifecycle, and holds the values set in the fields its type
  declares.
- type: A type is the definition of a kind of parcel: its lifecycle, the fields a parcel of it
  holds, and its bindings, which name the flow that starts when a parcel enters a stage and the
  gates a move must pass.
- lifecycle: A lifecycle is the set of stages a parcel of one type moves through and the moves
  allowed between them, with one or more entry stages, one paused stage, and exactly one terminal
  stage reached with a resolution; it lives on its type, so two types never share one.
- stage: A stage is one position in a type's lifecycle, such as refine, build, review, or closed,
  and a parcel stands at exactly one stage at a time.
- flow: A flow is the stored definition of steps, the transitions between them, and the caps on
  them (a budget, a spawn cap, and a visit cap on every transition), which a flow run follows.
- flow run: A flow run is one execution of a flow on one parcel; it starts, enters steps, may be
  paused, and ends with an outcome or is stopped, and it is always the two words, never a bare
  "run".
- agent: An agent is a configured AI worker: a stored definition of a persona, the vendor, model,
  and effort it runs on, its tools, and the answer schema it answers in.
- agent step: An agent step is a step in which the daemon spawns an agent and records the
  structured answer it returns, once that answer validates against the step's answer schema.
- interactive step: An interactive step is a step in which an agent works with a person in a live
  session before the step ends; it is settled as a word but not built, so no flow can declare one
  yet, and the Claude Code executor cannot send a live session a further message.
- human step: A human step is a step a person performs, with the flow run waiting until the person
  records it; it is settled as a word but not built, and today the nearest thing is the
  wait-for-human system step, which pauses the flow run with a lever until a person resumes it.
- system step: A system step is a step the daemon performs itself with no agent, such as worktree,
  gates, open-pr, gated-merge, rebase, timer, wait-for-human, wait-for-external-event, move-parcel,
  derive-parcels, or emit-outcome, and it ends with a result document the flow routes on or pauses
  the flow run.
- trigger: A trigger is what starts a flow run of a flow: an event kind plus equality filters, which
  a parcel entering a stage its type binds a flow to, a flow run ending with an outcome another flow
  follows, or an external event a person's subscription matches can satisfy.
- paused: Paused is the condition of work set aside, always with a reason and who paused it; a
  paused flow run records both and is resumed or stopped, while a parcel is paused today by moving
  it to its type's paused stage, which records who moved it but no reason, and the parcel's paused
  as a condition that keeps its stage is settled but not built.
- module: A module is a domain's code and definitions that extend the core, and software
  engineering is the first; it is settled as a word but the module boundary is not built yet, so
  the software engineering system steps, types, flows, and agents still ship inside the binary
  today.

## Rejected names

- For parcel: task, issue, ticket, card, item, work item, type, entity (Brian's list); piece
  (already means a sub-part of an idea in hall9k), matter (abstract, pun-prone), and about twenty
  others. A shipping parcel gives the rest of the vocabulary for free: addressed (owner, node),
  labelled (type), tracked (its stage), and handled (a flow run acts on it). "Package" is never
  used anywhere in Coanda's own vocabulary.
- For flow: "stream", which collides with event streams in the code and with data streaming in
  every developer's head.
- For stage, on 2026-11-03: "phase", which hall9k's board uses with a second meaning.
- For type: "kind" (Brian, 2026-11-03 11:23 EST: a parcel has a type, not a kind).

## History

Each entry is dated by the ruling. The code followed each ruling in a later change, and a record
written before such a change is refused by the daemon, which says how to move the node home aside
and start fresh.

### 2026-11-03: the first vocabulary

The first list, settled at 08:46 and 09:09 EST, used words that have since been replaced or
dropped:

- flow, for an agentic automation made of agent steps: the definition. On 2026-11-03 the
  definition became a contour, and flow named one execution; on 2026-11-04 the definition became a
  flow again.
- flight, for one execution of a flow on one parcel (11:31 EST, replacing "run" and "pass", and
  chosen over take, voyage, and pathline): replaced by flow later on 2026-11-03, and by flow run on
  2026-11-04.
- park and parked, for a flight held for later, and the parcel types' parked state: replaced by
  pause and paused on 2026-11-03, and the parked state became the paused stage.
- state, for one position in a lifecycle: stage was chosen over it on 2026-11-03 at 09:09 EST, but
  the code kept state (`States`, `Parcel.State`, `--state`) until the 2026-11-04 change below,
  which replaced it with stage.
- judgment, for a step where an agent is spawned and returns a structured answer: replaced by agent
  step on 2026-11-04.
- action, for a step the daemon does itself with no agent (Brian's pick over routine and reflex):
  replaced by system step on 2026-11-04.
  The registry's query, command, and action triad keeps the word, since it is not a step.
- round, for one repeat of a set of steps inside a flow, with caps on rounds: retired with no
  replacement. It never reached the code; caps sit on transitions as visit caps.
- step, query, and command were listed here as related fixed words. They are ordinary words in the
  code and the design and are not part of the settled vocabulary.
- lifecycle replaced the earlier "pipeline with middleware" idea, and is kept.

### 2026-11-03: contour, flow, pause, and model spec

Brian ruled on these reading the phase 1 review: "model triple" was confusing, "park" did not say
what was happening, and "flight" did not read as the execution of a flow.

- contour: the stored definition of steps, transitions, and caps (replacing flow in that sense, and
  the type name `FlowDefinition`). Retired on 2026-11-04 and replaced by flow, because contour did
  not read as the definition in practice.
- flow: one execution of a contour on one parcel (replacing flight). Replaced on 2026-11-04 by flow
  run.
- pause and paused: replaced park and parked, and are kept.
- model spec: the vendor, model, and effort an agent runs on, replacing "model triple". The code
  still says model spec (`ModelSpec`); model triple is retired.

### 2026-11-04: the concepts walk

Brian ruled on these in the concepts walk of 2026-11-04 and confirmed them in its recap.

- flow run replaces flow in its sense of one execution. The code names it `FlowRun` (with
  `FlowRunStarted`, `FlowRunPaused`, `FlowRunResumed`, `FlowRunEnded`, `FlowRunStopped`, and
  `Parcel.LiveFlowRunId`), the trigger kind `flow-ended` became `flow-run-ended`, and the principal a
  flow run acts as is `flow-run:<id>` rather than `flow:<id>`.
- flow replaces contour as the definition. The code names it `Flow` (with `FlowStep`,
  `FlowTransition`, `FlowFollow`, `FlowSource`, `FlowValue`, and `FlowBinding`), the definition kind
  is `Flow`, and the trigger field that names a flow run's flow on its end is `flow` rather than
  `contour`.
- stage replaces state for one position in a type's lifecycle. The code names it `Stages`,
  `EntryStages`, `TerminalStage`, `PausedStage`, `Parcel.Stage`, and `FlowBinding.Stage`, a move
  records `FromStage` and `ToStage`, the trigger field is `stage`, and the move-parcel system step's
  argument is `stage`. The stage named `paused` keeps its name.
- agent step replaces judgment, and system step replaces action, for the two step kinds built
  today. The code names them `StepKind.AgentStep` and `StepKind.SystemStep`, the API's step kind
  values are `agent-step` and `system-step` rather than `judgment` and `action`, a flow step names
  its system step in `SystemStep` rather than `Action`, and a system step's result names it in
  `systemStep` rather than `action`.
- interactive step and human step are new kinds of step, settled as words and not built.
- paused is one word whether a person or an agent set the work aside, always with a reason and who
  paused it. "On hold" was proposed in the walk for a parcel set aside and dropped for paused; it was
  never in the code, and it has no replacement other than paused.
- module names a domain's code and definitions that extend the core; "software bundle" is retired
  for it.
- model triple, contour, flight, park, and round are retired, as the entries above say.

### Commands and the API

Each `coa` command, option, and argument a renamed word changed, from the oldest name it had to the
one the help `coa` prints for that command now:

- `coa flight list --parcel <ID>` and `coa flight show <ID>` became `coa flow list` and
  `coa flow show <ID>` on 2026-11-03, and are now `coa flow-run list --parcel <ID>` and
  `coa flow-run show <ID>`.
- `coa flow pause <ID> --reason <REASON>`, `coa flow stop <ID> --reason <REASON>`, and
  `coa flow resume <ID> --note <NOTE>` were added after 2026-11-03, when flow meant one execution,
  and are now `coa flow-run pause`, `coa flow-run stop`, and `coa flow-run resume`, with the same
  arguments and options.
- `coa flow list` and `coa flow start <FLOW> --parcel <ID>`, for the definitions, became
  `coa contour list` and `coa contour start <CONTOUR> --parcel <ID>` on 2026-11-03, and are now
  `coa flow list` and `coa flow start <FLOW> --parcel <ID>` again.
- `coa parcel add --state <STATE>` is now `coa parcel add --stage <STAGE>`, and
  `coa parcel move <ID> <STATE>` is now `coa parcel move <ID> <STAGE>`; both date from the first
  command line.
- `coa type list`, `coa parcel list`, and `coa parcel show` label the column or line that held
  States or State as Stages or Stage, and `coa flow-run show` prints each step's kind as
  `agent-step` or `system-step` rather than `judgment` or `action`.

`coa flow list` has therefore had more than one meaning: it listed the definitions before
2026-11-03, it listed the executions from then until the flow run name on 2026-11-04, and it lists
the definitions again since the flow name.

The API moved the same way. The executions were served under `/flights`, then `/flows`, and are now
under `/flow-runs`; the definitions were served under `/flows`, then `/contours`, and are now under
`/flows` again. The registry entries for executions moved from `flow.*` to `flow-run.*`, and
`contour.list` became `flow.list`. A parcel's and a type's views name their stages `stage` and
`stages`, and adding a parcel takes `stage` rather than `state`.
