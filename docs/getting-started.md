# Getting started

Coanda is under construction. Phase 0 ships a binary, `coa`, that starts a daemon on your machine
and keeps its record in one SQLite file in your home directory.

```
coa project add try-coanda --repo git@github.com:you/try-coanda.git --branch main
coa parcel add --project try-coanda --type task --title "Try Coanda"
coa parcel list --project try-coanda
```

A project records the one repository its work acts on, its remote and its primary branch, so a
parcel can be created only in a project that `coa project add` has registered. The remote is
stored as given; nothing contacts it. Without `--project`, a command works in the project that
`COANDA_PROJECT` names, then in the one named after the current directory.

The lifecycle needs no configuring. The software engineering lifecycle is installed by default.

Judgments run on Claude Code. With no configuration at all, the daemon runs each judgment as a
headless session of the `claude` executable it finds on the PATH, under whatever login that
executable already has, so install Claude Code and sign in to it before a flow reaches its first
judgment. What a node runs its judgments with is configured once per node, in `node.json` in the
node home: the executor (`claude-code`, with an optional path to the `claude` executable), and the
credential profiles a project can name with `coa project set-profile`.
[Node configuration](node-configuration.md) describes the file.

If you ran a build from before 2026-11-03, its record does not carry over, and the daemon says so
when it starts. Move `~/.coanda` aside and run the commands above again. The commands that drive
flows also changed: `coa contour start <contour> --parcel <id>` starts a flow, and `coa flow list`
and `coa flow show` read them. [The overview](overview.md#operating) has the full list.
