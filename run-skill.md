Run skill shape: pointer. This repository already documents how to launch it; the steps below point at those files by path and add only what they leave out.

## Prerequisites

The repository's own launch coverage is `docs/getting-started.md` (the commands), `docs/overview.md` (the "Installing" and "Operating" sections), and `docs/node-configuration.md` (the node configuration file). Read those first. This skill adds only what they assume.

- **.NET 10 SDK.** `Directory.Build.props` pins `net10.0` and CI installs `10.0.x` (`.github/workflows`). There is no `global.json`, so any 10.x SDK works. Check with `dotnet --version`.
- **git.** The checkout itself needs it, and a project registered with `coa project add` records a git remote. Check with `git --version`.
- **Claude Code (`claude`), only for real judgments.** With no `node.json`, the daemon runs each judgment as a headless `claude` session found on the PATH, under that executable's existing login, and the session's spend is billed to that login (`docs/overview.md`, `docs/node-configuration.md`). Check with `claude --version`. It is not needed to start the daemon or to run the plain parcel commands.
- **No other service.** The store is one SQLite file under the node home, which the daemon creates itself. No container runtime, database server, or seed data is involved (`docs/overview.md`, "What Coanda is").

## One-time setup

Run everything from the repository root.

1. Restore and build. This is also the project's own build command:

   ```
   dotnet build
   ```

   Warnings are errors (`Directory.Build.props`), so a clean build is the baseline. The executable is `coa` (`AssemblyName` in `src/Coanda.Cli/Coanda.Cli.csproj`) and lands at `src/Coanda.Cli/bin/Debug/net10.0/coa` (`docs/overview.md`, "Installing"). On Windows the file is `coa.exe`.

2. Choose a node home before launching anything. The node home holds the database, the daemon's address and token file, the log, and `node.json`.
   - **Default:** `~/.coanda`. Use it only if you mean to touch the user's real record.
   - **Throwaway or dev (recommended for trying a change):** set `COANDA_HOME` to a directory. The repository ships `.coanda-dev/node.json` for this, and its executor is `scripted`, so every judgment pauses at no cost instead of starting a Claude Code session. The Rider run configurations in `.run/` use `COANDA_HOME=<repo>/.coanda-dev`.

   ```
   export COANDA_HOME="$PWD/.coanda-dev"
   ```

   In PowerShell: `$env:COANDA_HOME = "$PWD\.coanda-dev"`.

3. Executor choice (`docs/node-configuration.md`). `node.json` is optional. If it exists it must name `executor` as `claude-code` or `scripted`, and an unparsable or unknown-property file makes the daemon exit with code 2 and print the rule it refused. To run real Claude sessions in `.coanda-dev`, change `.coanda-dev/node.json` to `"executor": "claude-code"`, and do not commit that change by accident.

4. If a `~/.coanda` record predates 2026-11-03, the daemon refuses to start on it, naming the file. Move it aside (`mv ~/.coanda ~/.coanda.pre-rename`) and start again. A fresh home is created on first start (`docs/getting-started.md`, `docs/overview.md`).

## Launch

There is nothing to start by hand. `coa` is both the CLI and the daemon, and any `coa` command starts a detached daemon if none is running for that node home (`docs/overview.md`, "What Coanda is"; `src/Coanda.Cli/DaemonClient.cs`).

From the repository root, with `COANDA_HOME` set as above (or not set, for `~/.coanda`):

```
./src/Coanda.Cli/bin/Debug/net10.0/coa daemon start
```

Then exercise it (`docs/getting-started.md`):

```
./src/Coanda.Cli/bin/Debug/net10.0/coa project add try-coanda --repo git@github.com:you/try-coanda.git --branch main
./src/Coanda.Cli/bin/Debug/net10.0/coa parcel add --project try-coanda --type task --title "Try Coanda"
./src/Coanda.Cli/bin/Debug/net10.0/coa parcel list --project try-coanda
```

`coa project add` stores the remote as given and contacts nothing, so a placeholder URL is fine. A parcel can only be created in a registered project.

To run the daemon in the foreground instead, which blocks and logs to the console:

```
./src/Coanda.Cli/bin/Debug/net10.0/coa daemon run --port 0
```

`--port 0` picks a free loopback port; this is what the `.run/coa daemon run.run.xml` configuration uses (`src/Coanda.Cli/Commands/DaemonCommands.cs`). A detached start (`coa daemon start`, or any command starting it on demand) runs `coa daemon run --log-to-file` and writes to `coanda.log` in the node home. The CLI waits up to 15 seconds for it (`src/Coanda.Cli/DaemonClient.cs`).

Stop it with `coa daemon stop`. Pause any flow with a running judgment first, because a Claude session outlives the daemon (`docs/node-configuration.md`).

## How to know it is up

- `coa daemon start` prints `Started the daemon on <address> (pid <n>); log at <home>/coanda.log`, or `The daemon is already running on <address> (pid <n>).`
- `coa daemon status` prints a table with Version, Address, Pid, Started, Home, and Database. If nothing is running it prints `No daemon is running. Home: <path>`.
- A foreground `coa daemon run` prints `coanda daemon <version> listening on <address> (pid <n>), home <path>`.
- The daemon exposes `GET /health` on its loopback address, which returns `{"ok":true}` without a token. The address and port are in the `daemon.json` file in the node home; the port is chosen at start, so read it from there or from `coa daemon status`, not from this document.
- If the start times out, the message points at `coanda.log` in the node home, which also holds any node-configuration refusal.

## Address or entry point

- **CLI:** `src/Coanda.Cli/bin/Debug/net10.0/coa` (`coa --help` lists commands; `docs/overview.md`, "Operating" lists the main ones: `daemon`, `project`, `parcel`, `contour`, `flow`).
- **Daemon API:** loopback HTTP on a port the daemon picks, recorded in `daemon.json` in the node home. Every call except `/health` needs the token in that file, so use `coa` rather than raw HTTP.
- **Tests, which exercise the code without a running daemon:** `dotnet test --configuration Release --no-build` after a Release build, per the project registration. CI runs on ubuntu and windows (`AGENTS.md`).

## Human steps

- **Claude Code login.** For flows to run real judgments, someone has to install `claude` and sign in to it on the machine (`docs/getting-started.md`). The repository does not say which account or plan to use. For a non-default account, a profile in `node.json` with a `configDirectory` or `apiKeyHelper` is needed, followed by `coa project set-profile <project> <profile>`. Log in once with `CLAUDE_CONFIG_DIR` set to that directory. The repository contains no credential values, and any API key has to come from the operator's own secret store.
- **A real repository to point a project at.** `coa project add` needs a remote URL and branch. The `try-coanda` URL in the docs is a placeholder. Flows that open pull requests or merge will need whatever git and GitHub access that repository requires. The docs do not say what that is for this phase (see `docs/design/design.md` and `docs/design/prototype-plan.md`), so confirm it with the owner before running a real flow.
- **Port.** No fixed port is documented. The default is picked at start (`--port` defaults to 0). If a fixed port is needed, pass `--port` to a foreground `coa daemon run`; a detached start passes none.
- **Release binaries** are described as arriving in phase 5 (`docs/overview.md`). Until then there is no install path other than building from source.