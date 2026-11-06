# Phase 3 gate: the second node joins, claims, and builds

This is the record of phase 3's gate run (prototype-plan.md, phase 3): the Windows machine joins the
owner's project with `coa join`, verifies the owner's history, claims a task by arbitration, and
builds it to an open pull request. The run follows [joining a second node](../joining-a-second-node.md).
The record holds no token, credential, or private key.

The run has not happened yet. The person running it fills in each section below on this pull
request's branch before it merges.

## The ledger home

In phase 3 the ledger home has one kind, the project's own repos, each carrying the ledger under
`refs/coanda/nodes/`. No setting names it: the setting that would name another kind waits for the
relay (design section 6 and section 14.9).

## When, and what each machine ran

- Date:
- Owner's node (Mac): commit
- Windows machine: commit

## The commands and their results

For each step of the runbook: the machine, the command, and what it printed (trimmed of keys and
ids where they add nothing).

1. `coa node show` on the Windows machine:
2. `coa node show` on the owner's node:
3. `coa node vouch` on the owner's node:
4. `coa join` on the Windows machine, with the import's result for the owner's origin (lines stored
   and verified, the sequence held, and no fault):
5. `coa parcel list` on both machines, showing the same parcels:
6. The task added on the owner's node:
7. `coa parcel claim` on the Windows machine, reporting held:
8. `coa parcel move <id> build` on the Windows machine, and the build flow run's end, naming the
   pull request it opened:
9. `coa flow-run list --parcel <id>` on the owner's node, showing the Windows machine's flow run
   imported and none the owner's node started:

## What the first real Windows session needed

What had to be installed, configured, or fixed on the Windows machine before the run went through,
including the committer identity for the build agent's commits, and which of those fixes this pull
request made. A fix too large for this pull request is listed here as a follow-up instead.
