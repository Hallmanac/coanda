# Naming decisions for the start-over product (settled with Brian, 2026-11-03 08:46 EST)

Settled in the hall9k project window during the naming exploration. The design work itself lives in
the start-over exploration session; this file records only names and the reasons, so no session
re-litigates them.

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

## Concept vocabulary

Concept words are deliberately plain and borrowed; only the product name is unique.

- flow: an agentic automation made of agent steps. Superseded by the 2026-11-03 entry on contour,
  flow, pause, and model spec at the end of this file: the definition is now a contour, and flow
  names one execution of it. Not "stream" (collides with event streams in
  the code and with data streaming in every developer's head).
- lifecycle: the states a parcel moves through; a lifecycle defines one or more types of parcel
  that flows act on. Replaced the earlier "pipeline with middleware" idea.
- parcel: the generic unit of work. A fluid parcel is by definition the unit a flow acts on; a
  shipping parcel gives the rest of the vocabulary for free: addressed (owner, node), labelled
  (type), tracked (lifecycle state), handled (a flow acts on it), held (parked), bundled
  (grouped), with contents that flows add to. "idea" and "task" are two built-in types of
  parcel; users define other types. Never use "package" anywhere in Coanda's own vocabulary.
- type: the schema that defines a type of parcel (Brian, 2026-11-03 11:23 EST: a parcel has a type, not a kind; "kind" is retired).
- flight: one execution of a flow on one parcel; it starts, has steps, may hold (park), and ends with an outcome. "The third flight of the build flow on this parcel"; `coa flight list`, `coa flight show <id>`. Brian, 2026-11-03 11:31 EST, over take, voyage, and pathline; on theme because Henri Coandă was an aviation pioneer. Replaces "run". Superseded by the 2026-11-03 entry on contour, flow, pause, and model spec at the end of this file: one execution is now a flow.

Rejected for parcel: task, issue, ticket, card, item, work item, type, entity (Brian's list);
piece (already means a sub-part of an idea in hall9k), matter (abstract, pun-prone), and about
twenty others.

## Added 2026-11-03 09:09 EST

- stage: one position in a lifecycle (draft, build, pull request, QA, done). Chosen over
  "state" and "phase", which hall9k's board uses with two different meanings.
- step: one thing a flow does. A step is a judgment or an action.
- judgment: a step where an agent is spawned and returns a structured answer.
- action: a step the daemon does itself with no agent (worktree, gates, pull request, timer,
  wait for a human, emit the outcome, move a parcel to its next stage). Brian's pick over
  routine and reflex; the GitHub Actions collision is mild and never in the same sentence.
  Related fixed words: query reads the record, command changes the record.
- round: one repeat of a set of steps inside a flow; caps are on rounds.
- pass or run: superseded; the noun was flight (see above), and is now flow (see the entry on
  contour, flow, pause, and model spec below).
- Open seam for the design session: idea to task is a derivation between types (one idea spawns
  task parcels), not a stage change of one parcel, so a lifecycle holds stages within a type
  and derivations between types.

## Added 2026-11-03: contour, flow, pause, and model spec

Brian ruled on these on 2026-11-03, reading the phase 1 review: "model triple" was confusing,
"park" did not say what was happening, and "flight" did not read as the execution of a flow.

- contour: the stored definition of steps, transitions, and caps, which a flow follows. It replaces
  "flow" in its sense of the definition (and the type name `FlowDefinition`). The name comes from
  the same effect as the product's: a Coanda flow attaches to the contour of a surface and follows
  it.
- flow: one execution of a contour on one parcel; it starts, has steps, may pause, and ends with an
  outcome or is stopped. It replaces "flight". `coa flow list`, `coa flow show <id>`, and
  `coa contour start <contour> --parcel <id>`. Superseded by the 2026-11-04 entry on flow run at the
  end of this file: one execution is now a flow run.
- pause: what a flow does when it waits for a person, until an external event arrives, or because
  a cap or its budget is reached. A paused flow is live and is resumed or stopped. It replaces
  "park" and "parked", including the parcel types' parked state, which is now named paused.
- model spec: the vendor, model, and effort an agent runs on. It replaces "model triple".

The commands moved with the words: `coa flow start` became `coa contour start`, `coa flow list`
(the definitions) became `coa contour list`, and `coa flight list` and `coa flight show` became
`coa flow list` and `coa flow show`. The API moved the same way, from `/flows` and `/flights` to
`/contours` and `/flows`. A record written before the change does not carry over: its event names,
documents, and the hashed content of its types and contours use the old words. The daemon refuses
such a record without touching it and names the file; move the node home aside and start fresh.

## Added 2026-11-04: flow run

Brian ruled on this on 2026-11-04 in the concepts walk: one execution of a contour on a parcel is a
flow run.

- flow run: one execution of a contour on one parcel; it starts, has steps, may pause, and ends
  with an outcome or is stopped. It is always the two words, never a bare "run". It replaces "flow"
  in its sense of the execution. The CLI writes it `flow-run` (`coa flow-run list`,
  `coa flow-run show <id>`), the API serves it under `/flow-runs`, the registry entries are
  `flow-run.*`, and the code names it `FlowRun` (`FlowRunStarted`, `FlowRunPaused`,
  `FlowRunResumed`, `FlowRunEnded`, `FlowRunStopped`, and `Parcel.LiveFlowRunId`). The trigger kind `flow-ended` became `flow-run-ended`, and the principal a
  flow run acts as is `flow-run:<id>` rather than `flow:<id>`.

The commands moved with the word: `coa flow list` and `coa flow show` became `coa flow-run list`
and `coa flow-run show`, and the API moved from `/flows` to `/flow-runs`. `coa contour list` and
`coa contour start` keep their names. A record in which a build from before
this change started a flow holds that flow under the old event names (`flow_started` and its
siblings), so the daemon refuses it without touching it and names the file; move the node home aside
and start fresh. A record from that build that never started a flow holds nothing this change renamed,
and the daemon reads it as it is.
