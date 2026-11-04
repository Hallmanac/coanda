# Node configuration

A node reads one configuration file, `node.json` in its node home (`~/.coanda/node.json`, or the
directory `COANDA_HOME` names). The daemon reads it once, when `coa daemon run` starts, which is
also what `coa daemon start` launches. A change takes effect at the next start; nothing reloads it
while the daemon runs.

The file is optional. Without it, the daemon runs the scripted executor with no entries and holds
no credential profiles, so every judgment pauses.

```json
{
  "executor": "scripted",
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

`executor` names the executor judgments run through, by key, and it is required. The only key
today is `scripted`, which runs a scripted executor with no entries.

## Credential profiles

`profiles` is optional. It holds named credential profiles per vendor, and the only vendor a profile
may be held under is `claude-code`. Each profile has a `name` and exactly one of these:

- `configDirectory`: a Claude Code config directory, the login state a spawn runs with. A relative
  path is resolved against the node home, never against the directory you ran `coa` from, so
  `accounts/personal` above means `~/.coanda/accounts/personal`. A leading `~` is not expanded.
- `apiKeyHelper`: a command that prints an API key. The command must obtain the key from somewhere
  else, such as a password manager, and print it. It must never contain the key itself, because this
  file is not a secret store.

Profile names are compared exactly, after surrounding spaces are trimmed, so `work` and `Work` are
two profiles, and two entries named `work` and ` work` under one vendor are a duplicate.

The file holds paths and commands only. Nothing at start reads a config directory or runs a helper
command, and neither is checked for existence.

## When the daemon refuses the file

The daemon does not start, and exits with code 2, when the file cannot be parsed as JSON, holds a
property this page does not name (property names are case-sensitive), names no executor, or names an
executor other than `scripted`. It does the same for a profile under a vendor other than
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
