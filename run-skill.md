Run skill shape: pointer. This repository already documents how to launch it; the steps below point at those files by path and add only what they leave out.

## Prerequisites

- The .NET 10 SDK. The repository pins only the target framework `net10.0` (`Directory.Build.props`); CI installs `10.0.x` (`.github/workflows/ci.yml`). There is no `global.json`. Check with `dotnet --version`, which should print a 10.x version.
- `git`, to have the checkout.
- Nothing else. There is no container runtime, database server, or account. The record is a SQLite file the daemon creates itself (`docs/overview.md`).

The repository's own guidance is `docs/overview.md` (written for agents; read it in full first), `docs/getting-started.md` and `AGENTS.md` (the "Build, test, run" section). This skill adds only what they leave out.

## One-time setup

Run from the repository root (the directory containing `Coanda.slnx`):

```
dotnet build
```

That restores NuGet packages and builds the whole solution. Warnings are errors (`Directory.Build.props`), so a clean build is the expected result. Optionally run `dotnet test` to confirm the checkout is healthy. The tests are documented as never touching GitHub or creating branches (`AGENTS.md`).

The build produces the `coa` executable at `src/Coanda.Cli/bin/Debug/net10.0/coa` (`AGENTS.md`, `docs/overview.md`). Nothing installs it onto PATH. `docs/getting-started.md` writes bare `coa ...` commands, so either use the full relative path above or add that directory to PATH yourself.

There is no seed, migration, or configuration step. The software engineering lifecycle (the `idea` and `task` parcel types) is installed by default (`docs/getting-started.md`, `docs/overview.md`).

## Launch

There is nothing separate to start by hand. `coa` is both the CLI and the daemon, and any command that needs the daemon starts it detached if it isn't running (`src/Coanda.Cli/DaemonClient.cs`). From the repository root:

```
./src/Coanda.Cli/bin/Debug/net10.0/coa daemon start
```

Explicit alternatives (`src/Coanda.Cli/Commands/DaemonCommands.cs`):

- `coa daemon run` runs the daemon in the foreground, blocks, and logs to the console. This is the one to use when you want to watch it. Add `--port <PORT>` to pick a loopback port; the default of 0 picks a free one.
- `coa daemon stop` asks a running daemon to stop.

To keep a throwaway node apart from your real one, set `COANDA_HOME` to a directory before running any `coa` command. The default is `~/.coanda` (`src/Coanda.Daemon/NodeHome.cs`). The variable must be set for every command that should talk to that node, including `daemon stop`.

## How to know it is up

- `coa daemon status` prints a table with Version, Address, Pid, Started, Home and Database. If nothing is running, it prints "No daemon is running. Home: ..." (`src/Coanda.Cli/Commands/DaemonCommands.cs`).
- `coa daemon start` prints "Started the daemon on http://127.0.0.1:<port> (pid <pid>); log at <home>/coanda.log", or "already running" if it was up.
- A foreground `coa daemon run` prints "coanda daemon <version> listening on http://127.0.0.1:<port> (pid <pid>), home <path>".
- The daemon's `GET /health` on that address returns an HTTP success status with body `{"ok":true}` (`src/Coanda.Daemon/Api/DaemonHost.cs`). The CLI's own liveness check uses this endpoint.
- If a detached start fails, the CLI gives up after 15 seconds with an error naming the log file, `coanda.log` in the node home (`src/Coanda.Cli/DaemonClient.cs`).

## Address or entry point

- The entry point is the CLI binary `src/Coanda.Cli/bin/Debug/net10.0/coa`. Run `coa --help` for the command tree (`AGENTS.md`).
- First commands to exercise it (`docs/getting-started.md`, `docs/overview.md`): `coa parcel add --type task --title "Try Coanda"`, `coa parcel list`, `coa parcel show <id>`, `coa type list`.
- The daemon's loopback API is on `127.0.0.1` at a port chosen at start. Read it from `coa daemon status`, or from `daemon.json` in the node home. Calls carry a token header from that file, so use the CLI instead of calling it by hand.
- Per-node files, all under the node home (`~/.coanda` unless `COANDA_HOME` is set): `coanda.db` (the record; do not edit by hand), `daemon.json`, `coanda.log`.

## Human steps

- None required to run Phase 0. No secrets, logins, or accounts are needed.
- `AGENTS.md` and `docs/overview.md` say release binaries do not exist yet ("phase 5"). Until then every machine builds from source.
- The repository does not state the binary's file name or exact path on Windows, although CI builds there. If you run on Windows, check the build output under `src/Coanda.Cli/bin/` yourself.
- The repository does not say whether a Release build (CI uses `dotnet build --configuration Release`) should be run in place of the Debug path above. The documented path is Debug.