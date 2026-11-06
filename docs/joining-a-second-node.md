# Joining a second node: from a fresh Windows machine to a built pull request

This runbook takes a person from a fresh Windows machine to a pull request that the Windows machine
built, in a project that another machine (the owner's node, a Mac in phase 3's gate) already runs.
The Windows machine joins the owner's project, verifies the owner's history, claims a task by
arbitration, and builds it. Every step names the machine it runs on and what it should print. A step
marked **(person at Windows)** needs someone sitting at the Windows machine; nothing else in this
runbook can do it for them.

The commands below write `coa` for the build of `coa` this runbook asks for. On Windows that is
`src\Coanda.Cli\bin\Release\net10.0\coa.exe` in the checkout you build; on the Mac it is
`src/Coanda.Cli/bin/Release/net10.0/coa`. The project is written `coanda`; use your project's name.

## Before the run

Install and configure each of these first. A missing one shows up later as a paused flow run or a
refused command, and is slower to find there.

1. **The .NET SDK** (version 10) on the Windows machine and on the owner's node.
2. **A build of `coa` at the pull request's commit, on both machines.** `coa node vouch` and
   `coa join` exist only from that commit on, so the owner's node must run that build for the gate
   too. On each machine, from a clone of the repository:

   ```
   git fetch origin pull/<number>/head:join-gate
   git checkout join-gate
   dotnet build --configuration Release
   ```

   On the owner's node, stop the daemon its usual build runs before using this build (the CLI refuses
   to talk to a daemon of another version and says so): run `coa daemon stop` with the usual `coa`,
   then use this build's `coa` for every owner step below, which starts its own daemon on the same node
   home. "Afterwards", at the end, says how the owner's node returns to its usual build.
3. **Git, and its committer identity, on the Windows machine.** The build agent makes commits, and
   the rebase step does too, so git needs a name and an email for the account the daemon runs as:

   ```
   git config --global user.name "<your name>"
   git config --global user.email "<your email>"
   ```

4. **Git credentials for the exact form of the remote the project's registration records.** Read it
   on the owner's node:

   ```
   coa project show coanda
   ```

   The repo line prints the remote as it was registered, for example
   `ssh://git@github.com/Hallmanac/coanda.git`. The Windows machine joins with exactly that text and
   fetches and pushes with it, so it needs credentials for that form: an ssh key loaded in an agent,
   or one with no passphrase, for an ssh remote (the daemon runs ssh with `BatchMode=yes`, so nothing
   may prompt); a credential helper that already holds the credential for an https remote. Check it
   without the daemon: `git ls-remote <remote>` must print refs without asking for anything.
5. **A GitHub token for the software module on the Windows machine.** Git's push credentials do not
   cover GitHub's API, which opens the pull request. Either sign in the GitHub CLI
   (`gh auth login`, then `gh auth setup-git`), which the module reads with `gh auth token`, or select
   a credential profile for the module after the join (step 7 below). The profile is the module's,
   apart from git's credentials.
6. **The agent executor and its credential profile on the Windows machine.** Install Claude Code
   and sign in, and name the native `claude.exe` in the node configuration if it is not on the PATH.
   A project's credential profile is node-local, so the Windows machine names its own: declare the
   profile in `node.json` in the node home (`%USERPROFILE%\.coanda\node.json`), as
   [node configuration](node-configuration.md) describes, and name it for the project after the join
   (step 7 below).

## The run

### 1. On the Windows machine: show this node **(person at Windows)**

```
coa node show
```

It starts the daemon on a fresh home and prints three lines, never wrapped, so each value copies whole:

```
Node id:     0192....-....-7...-....-............
Public key:  <44 characters of base64, ending in =>
Fingerprint: sha256:<64 hex digits>
```

Copy the node id and the public key to the owner's node by copy and paste (a file, a message to
yourself, a shared clipboard). Do not retype the key: one wrong character vouches for a key no
machine holds.

### 2. On the owner's node: show the owner's fingerprint

```
coa node show
```

It prints the owner's node id, public key, and fingerprint in the same three lines. The person
carries the `Fingerprint:` value to the Windows machine themselves (written down, or read off the
owner's screen). It must not come from the remote: it is what lets the Windows machine tell the
owner's history from anyone else's.

### 3. On the owner's node: vouch for the Windows machine

```
coa node vouch <windows-node-id> <windows-public-key> --project coanda
```

Paste the id and key copied in step 1. The first vouch in a project records the owner's root
first, so it prints:

```
Recorded this node as the owner root of project coanda, and trusts its own key as the project's root.
Recorded a vouch in project coanda for node <windows-node-id> with the key <windows-public-key>.
The vouch is pushed.
```

When the last line instead says the vouch is recorded but not yet pushed, it names the push's
reason; fix it and run the same command again, which records nothing new and pushes again. A vouch
is refused, recording nothing, when the id or the key is the owner node's own, when the project
already pairs that id with another key or that key with another id, or when the key is not a key.

### 4. On the Windows machine: join **(person at Windows)**

```
coa join <remote> --root <fingerprint>
```

The remote is the text step 4 of "Before the run" read with `coa project show`, and the fingerprint
is the one the person carried from step 2. It prints:

```
Joined project coanda.
Node id:     <windows-node-id>
Public key:  <windows-public-key>
Origin <owner-node-id>: stored <n> lines, holds through line <n>
This node's key is trusted in the project: the owner's vouch for it has arrived.
```

The origin line is the import's verdict on the owner's history: every line stored, and no
`stopped:` reason after it. A join made before step 3 still succeeds and says the key is not yet
trusted; the owner's node then stores what the Windows machine records once the vouch is recorded.
A refused join names its case (no root record with that fingerprint, a chain that does not verify,
a remote that is not exactly the registration's, a project of that name already here, or a fetch
that failed) and leaves nothing behind, so fix the cause and run it again.

### 5. On both machines: compare the parcels

```
coa parcel list --project coanda --all
```

Both machines list the same parcels.

### 6. On the owner's node: add the task the gate builds

```
coa parcel add --project coanda --title "<a small, real change>"
coa parcel set <id> objective --project coanda --text "<what it should do>"
coa parcel set <id> criteria --project coanda --item "<how to tell it is done>"
```

Leave it at refine. Do not move it to build here: the move belongs to the machine that holds the
claim. The daemon pushes it within 15 seconds.

### 7. On the Windows machine: name this node's profiles for the project **(person at Windows)**

```
coa project set-profile coanda <claude-code-profile>
coa project module select-profile coanda software <github-profile>
```

The first names the agent executor's credential profile from `node.json`. The second is needed only
when the module's GitHub token comes from a profile rather than `gh`. Then confirm the task arrived:
`coa parcel show <id> --project coanda` prints it at refine. The owner's node pushes it within 15
seconds and this node fetches every 15 seconds, so it can take half a minute; until then the command
says no parcel matches the id, and so does the claim below, which looks the parcel up before it
fetches. Run it again until it prints the task.

### 8. On the Windows machine: claim the task **(person at Windows)**

```
coa parcel claim <id> --project coanda
```

It pushes the claim, fetches the owner's ledger, and prints:

```
Recorded this node's claim on parcel <id>; this node holds it.
```

When it says the claim is not yet held, it names why (a push or a fetch that failed, usually); run
the same command again once that is fixed. Go on only once it reports held.

### 9. On the Windows machine: move the task to build **(person at Windows)**

```
coa parcel move <id> build --project coanda
```

The move starts the build flow run on the Windows machine, which holds the parcel. Follow it with
`coa flow-run list --parcel <id> --project coanda` and `coa flow-run show <flow-run-id> --project coanda`.
It ends with the outcome `pr-opened`, and `coa parcel show <id> --project coanda` prints the pull
request in the task's `pull-request` field. A flow run that pauses names its reason and the lever
that resumes it; fix the cause and run `coa flow-run resume <flow-run-id> --project coanda`.

### 10. On the owner's node: confirm it started nothing

```
coa flow-run list --parcel <id> --project coanda
```

It lists the Windows machine's flow run, imported from its ledger, and no flow run the owner's node
started: the owner's node does not hold the parcel, so its trigger loop starts nothing on it.

## Afterwards

The owner's node returns to its usual build by stopping this build's daemon and letting the usual
`coa` start its own: run `coa daemon stop` with this runbook's build, then any command with the
usual `coa`. Once this pull request merges, the usual build is rebuilt from the primary branch and
carries the join and the vouch itself. Nothing in the node home needs undoing: the owner root and
the vouch are records of the project, and the root key record is the owner's own.

## When a vouch was a mistake

No command in this phase revokes a node or removes a vouch. A vouch for the wrong key (a typo, the
wrong machine) is recovered by giving the Windows machine a fresh home: stop its daemon, move its
node home aside (`%USERPROFILE%\.coanda`, or the directory `COANDA_HOME` names), and start again
from step 1, which makes a new node id and key for the owner to vouch for. The mistaken vouch stays
in the project's record and names a key nobody holds.
