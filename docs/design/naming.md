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

- flow: an agentic automation made of agent steps. Not "stream" (collides with event streams in
  the code and with data streaming in every developer's head).
- lifecycle: the states a parcel moves through; a lifecycle defines one or more types of parcel
  that flows act on. Replaced the earlier "pipeline with middleware" idea.
- parcel: the generic unit of work. A fluid parcel is by definition the unit a flow acts on; a
  shipping parcel gives the rest of the vocabulary for free: addressed (owner, node), labelled
  (type), tracked (lifecycle state), handled (a flow acts on it), held (parked), bundled
  (grouped), with contents that flows add to. "idea" and "task" are two built-in types of
  parcel; users define other types. Never use "package" anywhere in Coanda's own vocabulary.
- type: the schema that defines a type of parcel (Brian, 2026-11-03 11:23 EST: a parcel has a type, not a kind; "kind" is retired).
- flight: one execution of a flow on one parcel; it starts, has steps, may hold (park), and ends with an outcome. "The third flight of the build flow on this parcel"; `coa flight list`, `coa flight show <id>`. Brian, 2026-11-03 11:31 EST, over take, voyage, and pathline; on theme because Henri Coandă was an aviation pioneer. Replaces "run".

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
- pass or run: superseded; the noun is flight (see above).
- Open seam for the design session: idea to task is a derivation between types (one idea spawns
  task parcels), not a stage change of one parcel, so a lifecycle holds stages within a type
  and derivations between types.
