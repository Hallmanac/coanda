# Node configuration

A node reads one configuration file, `node.json` in its node home (`~/.coanda/node.json`, or the
directory `COANDA_HOME` names). The daemon reads it once, when `coa daemon run` starts, which is
also what `coa daemon start` launches. A change takes effect at the next start; nothing reloads it
while the daemon runs.

The file is optional. Without it, the daemon runs every judgment on Claude Code, starting the
`claude` executable it finds on the PATH, and holds no credential profiles.

```json
{
  "executor": "claude-code",
  "claudeExecutable": "/opt/claude/bin/claude",
  "profiles": {
    "claude-code": [
      { "name": "work", "configDirectory": "/Users/you/.claude-work" },
      { "name": "personal", "configDirectory": "accounts/personal" },
      { "name": "ci", "apiKeyHelper": "op read op://ci/anthropic/api-key" }
    ]
  }
}
```

## The executor

`executor` names the executor judgments run through, by key, and it is required when the file
exists. There are two keys:

- `claude-code` runs each judgment as a headless Claude Code session: a `claude` process in print
  mode, started in the flow's work directory under the node home. `claudeExecutable` is optional
  and names the executable to start. A bare name such as `claude` is looked up on the PATH of the
  daemon's environment when a judgment spawns; a path is used as given, and a relative one is
  resolved against the node home. Without it, the daemon starts `claude` from the PATH. On Windows,
  name the native `claude.exe`: a `.cmd` or `.bat` file is refused when a judgment spawns, because
  it would run through a command interpreter, and the flow pauses naming the path.
- `scripted` runs a scripted executor with no entries, so every judgment pauses. It takes no
  `claudeExecutable`.

### How a Claude Code session runs

Each judgment's session gets the agent's persona as the text appended to Claude Code's system
prompt, the model and effort its model spec names, and exactly the tools its tool set maps to (read
to Read; search to Glob and Grep; edit to Edit and Write; shell to Bash; web to WebFetch and
WebSearch), all of them pre-approved. Any call that would need a permission answer is denied rather
than left waiting, whatever your own Claude Code settings say. No user, project, or local settings,
hooks, or MCP servers are loaded, and automatic memory is off, so a session never reads or writes
your own Claude Code configuration. The repository's `CLAUDE.md` and `AGENTS.md` are not loaded
either (seen with Claude Code 2.1.318), so a session reads the project's guidance only when its
persona tells it to, as the shipped build agent's does.

Before each spawn the daemon writes `.coanda/task.md` (the parcel record) and `.coanda/prompt.md`
(the context the step carries) at the root of the work directory, and the session's persona as
`.coanda/persona.md`. It writes them fresh at every spawn, writes the persona again before the
retry resumes the session, and never reads any of them back. Each session's
stream output is kept as a transcript in `transcripts/<project>/<flow id>/` under the node home,
outside the work directory; a judgment that gives no valid answer after its one retry pauses with
that transcript's path as its lever.

Each session runs under a spend cap: what is left of the flow's budget, or of its chain's budget
when that is smaller. Claude Code checks the cap between turns, so one turn can go past it. A
session that ends with an error (its spend cap reached, or no login) is not retried; the flow pauses
quoting the error, and what the session spent is still recorded on the flow.

Stopping the daemon does not end a Claude Code session it is running: the session runs on, under
its spend cap, but its output no longer reaches the transcript. The step that was in progress
records nothing and is performed again at the next start. The daemon cannot yet reattach to a
session across a restart, so that next start runs a new session in the same work directory while
the old one may still be running, and what the old one spends is not recorded. To avoid that, pause
a flow whose judgment is running before you stop the daemon; a pause ends the session and records
what it spent.

The daemon removes from a session's environment the variables an enclosing Claude Code session sets
for itself, so a daemon started from inside Claude Code runs its sessions as independent ones.

## Credential profiles

`profiles` is optional. It holds named credential profiles per vendor, and the only vendor a profile
may be held under is `claude-code`. Each profile has a `name` and exactly one of these:

- `configDirectory`: a Claude Code config directory, the login state a spawn runs with, which the
  session receives as `CLAUDE_CONFIG_DIR`. A relative path is resolved against the node home, never
  against the directory you ran `coa` from, so `accounts/personal` above means
  `~/.coanda/accounts/personal`. A leading `~` is not expanded. Log in under the directory once,
  by running `claude` with `CLAUDE_CONFIG_DIR` set to it. A login made without that variable does
  not count: on macOS, a profile naming the default `~/.claude` explicitly reports that it is not
  logged in (seen with Claude Code 2.1.318).
- `apiKeyHelper`: a command that prints an API key, which the session receives as Claude Code's
  `apiKeyHelper` setting. The command must obtain the key from somewhere else, such as a password
  manager, and print it. It must never contain the key itself, because this file is not a secret
  store.

When a spawn runs under a profile, its environment holds none of the credential variables Claude
Code would prefer to the profile (`ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`,
`CLAUDE_CODE_OAUTH_TOKEN`, and the like), even if the daemon inherited them. A spawn with no profile
runs with the daemon's environment and whatever login the `claude` executable already has.

Profile names are compared exactly, after surrounding spaces are trimmed, so `work` and `Work` are
two profiles, and two entries named `work` and ` work` under one vendor are a duplicate.

The file holds paths and commands only. Nothing at start reads a config directory or runs a helper
command, and neither is checked for existence.

## When the daemon refuses the file

The daemon does not start, and exits with code 2, when the file cannot be parsed as JSON, holds a
property this page does not name (property names are case-sensitive), names no executor, names an
executor other than `claude-code` or `scripted`, or gives a `claudeExecutable` under `scripted`. It does the same for a profile under a vendor other than
`claude-code`, a profile with a blank name, two profiles with the same name under one vendor, and a
profile that does not hold exactly one non-blank `configDirectory` or `apiKeyHelper`.

The rule it prints names the file and the entry it refused. A foreground `coa daemon run` prints it
to the console. A detached start writes it to the node home's log, `coanda.log`, and `coa daemon
start`, like any command that starts the daemon on demand, checks the file first and prints the same
rule.

## Naming a project's profile

```
coa project set-profile try-coanda work
coa project show try-coanda
```

`coa project set-profile <project> <profile>` names the profile the project's judgments spawn with on
this node; only the node's owner may run it. The project must be registered, and the profile must
be one the running daemon's configuration holds. A later call replaces the name, and `coa project
show` prints it, or says that none is set.

The setting belongs to this node and is never part of what the project replicates. A judgment's spawn
receives the profile with that name under the vendor of its agent's model spec. When the project
names no profile, the spawn receives none. When it names one the configuration no longer holds under
that vendor (because the file changed between starts), nothing is spawned and the flow pauses with a
reason naming the profile and the vendor; name a held profile with `coa project set-profile` and
`coa flow resume` performs the step again.
