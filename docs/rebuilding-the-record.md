# Rebuilding a node's record from an export

A node's record is the SQLite database `coanda.db` in its node home (`~/.coanda`, or the directory
`COANDA_HOME` names). Builds before phase 3's gate wrote some events in shapes later builds no longer
write. The current build reads none of them: its daemon, its detached start, and its `coa record
export` refuse a home that still holds one, and the refusal points here.

The steps on this page rebuild such a record so that it holds current shapes only. Run them with the
build at commit `ee75e50cea5bacb69c9326da51e88836573d9e9d` on main, the last build that still reads
every earlier shape; it holds both commands below. Once the record is rebuilt, go back to the current
build. The current build's own `coa record export` and `coa record rebuild` work the same way on a
home that already holds current shapes, writing every event as the record holds it.

Two commands do the work:

- `coa record export` writes a full export of the record to `exports/record-<time>.json` under the
  node home. The file is readable and writable by its owner alone from the moment it is created. It
  holds every event of every project and of the node, each in this build's current shape, and every
  document the daemon keeps that is not regenerated from the events (the trigger loop's and the
  module feeds' positions, the ledger's line records, deferred follows, and the session and module
  token records, which hold token hashes only). It names the record's last event by its id and its sequence. The export is
  never pushed anywhere, and the command writes nothing to any clone or remote.
- `coa record rebuild <export>` builds a new record from the export, moves the existing database and
  its companion files (`coanda.db-wal`, `coanda.db-shm`, `coanda.db-journal`, whichever exist) into
  `record-before-rebuild-<time>/` in the node home, unchanged and still owner-only, and puts the new
  database in its place. It prints where it put the old one. Every other file in the home stays
  where it was.

Both refuse, and change nothing, while a daemon runs on the home, including one that has not
finished starting. The rebuild also refuses when it cannot read the export, and when the record's
last event is not the one the export names, which means the record has moved since the export was
written. A rebuild that fails part way leaves the home as it found it.

The export carries the record's events in order, and each keeps its id, its stream, its project, and
the time the store recorded it. In the build at `ee75e50`, three kinds of event change shape on the way:

- A definition recorded in the scope builds before module scope called `Shipped` is written in
  module scope, as the software module's at version 0.1.0. A fork base recorded as a name and a
  version number is written as the name, module, module version, and content hash of the definition
  it resolves to on this node. If one resolves to nothing, the export stops, writes no file, and
  names the definition.
- A flow run event naming one of the software steps without the `software/` prefix names it with the
  prefix. A flow's own content is written as stored, so a flow that names the steps without the
  prefix keeps its content and its content hash, and so do the flow runs that recorded that hash.
- A project registration from before repos is written with its one repo and without the remote,
  primary branch, and build and test commands, and the event that set the merge's required checks is
  left out. The export first carries what both held into the software module's settings for the
  project, as a start of this build does, so they reach the rebuilt record whether or not this build
  has started on the home before.

Because an event can be left out, the export numbers its events afresh. Each loop position is moved
with them: it names the same event it named before, or, when that event was left out, the last
exported event before it. The loops therefore handle no event a second time and pass over none they
had not reached. The ledger's line records are carried as they are, each line's bytes, chain sequence,
and hash unchanged, except for the store sequence each one records, which is where the next ledger
export reads from: it is moved the same way, so the export after a rebuild writes every event that
had not yet reached a line.

## Before you start

Build `coa` from commit `ee75e50cea5bacb69c9326da51e88836573d9e9d` on main and use that `coa` for every
command until the rebuilt home has been checked (for example `git checkout ee75e50` in a clone, then
`dotnet build` and `./src/Coanda.Cli/bin/Debug/net10.0/coa`). Do everything below as the user who owns
the node home. On Windows, run the commands in PowerShell.

While the daemon still runs, write down what the node reads, so you can compare the rebuilt record
with it later. For each project:

```bash
coa project list
coa project show <project>
coa project module show <project> software
coa parcel list --project <project> --all
coa flow list --project <project>
coa agent list --project <project>
coa type list --project <project>
```

For any parcel with flow runs, add `coa flow-run list --parcel <id>`. Saving the output to a file
makes the comparison quick (for example `coa project list > before-projects.txt`).

## Export and rebuild

1. Stop the daemon, and check that nothing is running on the home:

   ```bash
   coa daemon stop
   coa daemon status
   ```

   `coa daemon status` should say that no daemon is running. A daemon from a build before this one
   does not hold the home's lock, so make sure it has stopped before you go on: `coa record` refuses
   a daemon it can reach, but it cannot see an older daemon that is still starting.

2. Optionally, copy the whole node home somewhere safe first. The rebuild keeps the old record, but a
   copy also covers anything you do by hand afterwards.

   ```bash
   cp -Rp ~/.coanda ~/.coanda.before-rebuild
   ```

   ```powershell
   Copy-Item -Recurse "$HOME\.coanda" "$HOME\.coanda.before-rebuild"
   ```

3. Write the export:

   ```bash
   coa record export
   ```

   It prints how many events the export holds, the record's last event, the export's path, and the
   rebuild command to run next. It refuses if the daemon is running or starting.

   Run no other `coa` command between the export and the rebuild: every other command starts the
   daemon when it is not running, and the daemon writes to the record as it starts.

4. Rebuild the record from it, giving the path the export printed:

   ```bash
   coa record rebuild ~/.coanda/exports/record-<time>.json
   ```

   It prints how many events the rebuilt record holds and the directory the old record was moved to,
   `record-before-rebuild-<time>/` under the node home. Keep that directory until you are satisfied
   with the rebuilt record.

   If the rebuild refuses because the record's last event is not the one the export names, something
   wrote to the record after the export (most often a daemon start), or the record was already rebuilt
   from that export. Nothing was changed. Stop the daemon, run `coa record export` again, and rebuild
   from the new export.

## Check the rebuilt home

1. Start the daemon and run the same commands you ran before you started:

   ```bash
   coa daemon start
   coa project list
   coa project show <project>
   coa project module show <project> software
   coa parcel list --project <project> --all
   coa flow list --project <project>
   coa agent list --project <project>
   coa type list --project <project>
   ```

   Each should print what it printed before. The software module's settings now hold the primary
   branch, build and test commands, and required checks that the earlier project record held. A
   parcel that sat past the trigger loop's position before the rebuild is still past it, and the
   daemon starts no flow run it would not have started on the old record.

2. If `sqlite3` is installed, you can check that the rebuilt record holds none of the earlier shapes.
   Each of these prints `0`:

   ```bash
   sqlite3 ~/.coanda/coanda.db "SELECT count(*) FROM fi_events WHERE type = 'project_required_checks_set'"
   sqlite3 ~/.coanda/coanda.db "SELECT count(*) FROM fi_events WHERE type = 'definition_drafted' AND json_extract(data, '$.scope') = 'Shipped'"
   sqlite3 ~/.coanda/coanda.db "SELECT count(*) FROM fi_events WHERE type = 'definition_drafted' AND json_extract(data, '$.forkBase.version') IS NOT NULL"
   sqlite3 ~/.coanda/coanda.db "SELECT count(*) FROM fi_events WHERE type = 'project_registered' AND json_extract(data, '$.remote') IS NOT NULL"
   sqlite3 ~/.coanda/coanda.db "SELECT count(*) FROM fi_events WHERE type IN ('flow_run_started', 'step_started', 'system_step_dispatched', 'system_step_completed') AND (json_extract(data, '$.firstStepPerformer') IN ('worktree', 'gates', 'open-pr', 'gated-merge', 'rebase') OR json_extract(data, '$.performer') IN ('worktree', 'gates', 'open-pr', 'gated-merge', 'rebase') OR json_extract(data, '$.systemStep') IN ('worktree', 'gates', 'open-pr', 'gated-merge', 'rebase'))"
   ```

   Flow content that names the software steps without the prefix, and an `agent_step_spawned` with
   no agent hash, model, or launch id, are current shapes and stay as they were.

3. Once you are satisfied, delete the export and the moved-aside directory, or keep them in the node
   home, where only you can read them. Never commit or push the export.

4. Stop the daemon with `coa daemon stop`, go back to the current build's `coa`, and start the daemon
   with `coa daemon start`. It starts on the rebuilt home without refusing it.

## Put the old record back

If the rebuilt record is wrong, put the moved-aside record back. Nothing else in the home changed,
so this restores the node as it was before the rebuild.

1. Stop the daemon: `coa daemon stop`.
2. Move the rebuilt database and its companion files out of the way, then move the old record's files
   back into the node home:

   ```bash
   cd ~/.coanda
   mkdir -m 700 rebuilt-record
   mv coanda.db* rebuilt-record/
   mv record-before-rebuild-<time>/* .
   rmdir record-before-rebuild-<time>
   ```

   ```powershell
   Set-Location "$HOME\.coanda"
   New-Item -ItemType Directory rebuilt-record
   Move-Item coanda.db* rebuilt-record
   Move-Item record-before-rebuild-<time>\* .
   Remove-Item record-before-rebuild-<time>
   ```

3. Start the daemon with `coa daemon start` and check the home as above. The `rebuilt-record`
   directory can be deleted once you no longer need it.
