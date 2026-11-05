# Coanda: agent and contributor guide

Canonical guidance for anyone, human or agent, working in this repository.

## Build, test, run

```bash
dotnet build
dotnet test
./src/Coanda.Cli/bin/Debug/net10.0/coa --help
```

CI builds and tests on ubuntu and windows for every push and pull request to main.

## Coding standards

- Seal every class and record unless something inherits from it.
- Events are `public sealed record`, past tense, one per file, positional style.
- Aggregates are `public sealed class` with private setters; state changes only through `Apply`.
- IDs are UUIDv7 via UUIDNext. Never `Guid.NewGuid()`.
- Timestamps are `DateTimeOffset`, carried explicitly on events.
- File-scoped namespaces; namespaces mirror folders.
- Explicit types when the right-hand side is a method call; `var` only when the type is apparent.
- `switch` expressions and pattern matching over `if`/`else if` chains.
- Every new async method takes a `CancellationToken` as its last parameter and passes it on.
- No null-forgiving `!` where a prior check guarantees non-null; no unused usings.
- Outbound HTTP from the daemon goes through `IHttpClientFactory` typed clients registered in the service collection, never a client created per call.
- Warnings are errors.
- Tests assert with xUnit's built-in `Assert` and add no assertion library.

## Writing

No em dashes. Full sentences. No AI attribution in commits, pull requests, or docs.

## Design

`docs/design/design.md` is the design every change must fit; `docs/design/prototype-plan.md` is the
phase plan with each phase's gate. A build session reads the sections its task names before writing
code, and `decisions.md` in its worktree carries the binding rules.

## Layout

- `src/Coanda.Core`: the meta-model (parcels, types, relations), definitions (types, flows,
  agents), the interpreter, the registry. No store dependency.
- `src/Coanda.Daemon`: the store (Fisher on SQLite), loops, adapters, transport, the loopback API.
- `src/Coanda.Sdk`: the module SDK, referencing no other Coanda project: the module API's
  contract types, a client for the core's loopback API, and the command runner and git wrapper,
  which the daemon uses too.
- `schemas/module/v1`: the module API's JSON schemas, with `index.json` naming each call's documents.
- `src/Coanda.Cli`: the `coa` executable, thin; also hosts the daemon under `coa daemon`.
- `tests/Coanda.Tests`: unit tests. No test creates a branch or touches GitHub.
