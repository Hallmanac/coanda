# Phase 3 gate: the second node joins, claims, and builds

This is the record of phase 3's gate run (prototype-plan.md, phase 3): the Windows machine joins the
owner's project with `coa join`, verifies the owner's history, claims a task by arbitration, and
builds it to an open pull request. The run follows [joining a second node](../joining-a-second-node.md).
The record holds no token, credential, or private key.

The gate passed on 2026-11-08. The Windows machine joined, verified and stored the owner's seven
lines, held the claim on a task the owner's node added, and built it to pull request #81 through its
own build flow run. The owner's node imported that flow run and started none of its own. Getting the
Windows gates to pass needed two environment workarounds for test-suite defects the run exposed;
both are described below, with the follow-up tasks that fix them.

## The ledger home

In phase 3 the ledger home has one kind, the project's own repos, each carrying the ledger under
`refs/coanda/nodes/`. No setting names it: the setting that would name another kind waits for the
relay (design section 6 and section 14.9).

## When, and what each machine ran

- Date: 2026-11-08, from 09:24 to 15:05 EST. The Windows node's identity was first shown on
  2026-11-07 at 12:32 EST, when its home was created.
- Owner's node (Mac, macOS on arm64, 14 cores): commit 38ed00d85a1fe0a1ae55a07481cd4c3e455007c7, a
  Release build of this pull request's head.
- Windows machine (Windows 11 Pro 10.0.26300, 28 logical CPUs, .NET SDK 10.0.301, Git 2.56.0,
  GitHub CLI 2.83.1, Claude Code 2.1.319 as the native `claude.exe`): commit
  38ed00d85a1fe0a1ae55a07481cd4c3e455007c7, a Release build of this pull request's head.
- The project's one repo and its registered remote: `coanda`, `https://github.com/Hallmanac/coanda.git`.

## The commands and their results

1. `coa node show` on the Windows machine started the daemon on a fresh home and printed its node
   id (`01a1b711-61a5-7055-8d33-5ee00d4e4ebc`), public key, and fingerprint. The id and key reached
   the owner's node by copy and paste.
2. `coa node show` on the owner's node printed node id `01a1bb8c-2a67-7034-8c3b-80bd12b629d9` and
   fingerprint `sha256:14ad6637e89a16339f5d576665bef1fd78dbb77a51fac0a884fdd0fa759132cf`. The person
   carried the fingerprint to the Windows machine from the owner's screen.
3. `coa node vouch 01a1b711-61a5-7055-8d33-5ee00d4e4ebc <key> --project coanda` on the owner's node
   printed:

   ```
   Recorded this node as the owner root of project coanda, and trusts its own key as the project's root.
   Recorded a vouch in project coanda for node 01a1b711-61a5-7055-8d33-5ee00d4e4ebc with the key <key>.
   The vouch is pushed.
   ```

   Afterwards the remote held `refs/coanda/nodes/01a1bb8c-2a67-7034-8c3b-80bd12b629d9`.
4. `coa join https://github.com/Hallmanac/coanda.git --root sha256:14ad6637...` on the Windows
   machine printed:

   ```
   Joined project coanda.
   Node id:     01a1b711-61a5-7055-8d33-5ee00d4e4ebc
   Public key:  <key>
   Origin 01a1bb8c-2a67-7034-8c3b-80bd12b629d9: stored 7 lines, holds through line 7
   This node's key is trusted in the project: the owner's vouch for it has arrived.
   ```

   The import stored and verified all seven of the owner's lines, held through line 7, and reported
   no fault.
5. `coa parcel list --project coanda --all` printed `No open parcels in coanda.` on both machines:
   the project was new and held no parcels yet, so both listed the same (empty) set.
6. The task added on the owner's node: parcel `0d157d01`, "Section 2 of the prototype plan names no
   personal path and says the repository is public", at `refine`, with an objective, two criteria,
   and a non-goal. The Windows machine printed it at `refine` on its first `coa parcel show`.
7. `coa parcel claim 0d157d01 --project coanda` on the Windows machine printed
   `Recorded this node's claim on parcel 0d157d01; this node holds it.`
8. `coa parcel move 0d157d01 build --project coanda` on the Windows machine printed
   `Moved 0d157d01 from refine to build.` and started build flow run `8253a9c0`
   (`01a1bc2f-4f6e-71b2-8706-cc628253a9c0`). It ended with status `ended` and outcome `pr-opened`:
   worktree done, build done, gates failed three times and passed on the fourth run (see below),
   fix done each time with no code change, adversarial review clean, conformance review clean,
   open-pr opened [#81](https://github.com/Hallmanac/coanda/pull/81) (head `3ede9ea`, one file,
   `docs/design/prototype-plan.md`, +3 -4), and move-to-review moved the parcel to `review` with
   `pull-request` 81 and `repos` set to `coanda`. The move to review then started merge flow run
   `00de1af7` on the Windows machine, which paused at `gated-merge` because the project's
   `required-checks` setting is unset; it merged nothing and was left paused, as the gate ends at
   the opened pull request.
9. `coa flow-run list --parcel 0d157d01 --project coanda` on the owner's node listed exactly the
   Windows machine's two flow runs, imported from its ledger: `8253a9c0` (build v1, ended) and
   `00de1af7` (merge v1, paused). The owner's node started none: it does not hold the parcel.

## What the first real Windows session needed

Configuration on the Windows machine before the run:

- The .NET 10 SDK, Git, the GitHub CLI, and Claude Code were installed, and Claude Code was signed in.
  The native `claude.exe` was on the PATH, so no `node.json` was needed and no credential profile was
  named: the daemon used the default Claude Code sign-in and the module read its token with
  `gh auth token`.
- Git's committer identity was already set globally.
- Git used Git Credential Manager for github.com. `gh auth setup-git` made it use the GitHub CLI's
  credential, after which `git ls-remote` on the remote printed refs without prompting.

On the owner's node, the existing node home was written by a build from before the stage and step
names, which this build refuses to read. It was backed up and moved aside, as the CLI advises, and
the owner's node ran on a fresh home where `coanda` was registered with
`coa project add coanda --repo https://github.com/Hallmanac/coanda.git --branch main --build "dotnet build --configuration Release" --test "dotnet test --configuration Release --no-build"`.
Until then the remote had no `refs/coanda/*` at all.

The Windows gates failed three times on the test command, each time on a different set of 15 to 18
tests out of 2,128, and the fix agent rightly changed nothing for a one-section documentation
change. Two test-suite defects were behind it, and the run went through on two workarounds:

- `ToolGuardTests.An_escaped_redirection_before_an_ampersand_still_ends_the_command` fails every time
  when the worktree and `%TEMP%` are on the same drive, because the guard resolves the shell word `\`
  to that drive's root and refuses for a different reason than the test expects. GitHub's
  `windows-latest` runner keeps the checkout and the temp directory on different drives, so CI never
  saw it. Workaround: the daemon was restarted with `TEMP` and `TMP` on another drive, and Windows
  Defender exclusions were added for the node home and that temp folder only.
- The remaining failures were flow-run tests whose fixed 15-second wait in `FlowRunDriver` ran out
  while 28 parallel test threads each started daemons and agent processes. The temp-drive workaround
  did not change them. Workaround: the daemon was restarted with its processor affinity limited to
  four logical CPUs, which its child processes inherited, and the gates passed on the next run. The
  owner's node had shown the same timeouts earlier under heavy background load.

Each workaround changed only the one daemon process's environment and two Defender exclusions; no
user or system setting, no code, and no project setting. This pull request made none of these fixes,
because each needs its own change and test:

- Follow-up 6d74ae5d: make the ToolGuard test pass whatever drive the checkout and temp directory
  are on, with a same-drive case that runs on every OS.
- Follow-up c9a9470e: make the flow-run tests' wait survive a loaded or many-core machine, while a
  test that truly hangs still fails.
- Smaller follow-ups for the phase 3 sweep: `coa parcel set <id> criteria --item` replaces the whole
  list, so two calls keep only the last item; `coa parcel list --all` on an empty project says "No
  open parcels", which reads oddly under `--all`; and a merge flow run started on the claiming node
  pauses at `gated-merge` until the project's `required-checks` setting is given a value.
