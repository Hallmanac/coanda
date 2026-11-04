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
persona tells it to, as every shipped agent's persona does.

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

Stopping the daemon does not end a Claude Code session it is running. Each session's process is
started apart from the daemon: it reads its prompt from a file, writes its output straight into
the transcript, and is in a process group of its own (on Windows, it has a hidden console of its
own), so neither the daemon's exit nor an interrupt you type in the daemon's terminal reaches it.
The daemon keeps a record of each running session in `sessions/` under the node home. When the
daemon starts again, a flow whose judgment was running reattaches its session: the daemon collects
the session's answer, retrying it once if it is invalid, and records it with what the whole session
spent, without launching the agent again. A session the daemon stopped during its retry is not
retried a second time, and the turn before the retry counts toward what it spent. The judgment's
wall-clock limit still runs from when its session was launched. A session that finished while the
daemon was down is collected the same way, even when the limit has passed since: it ran no longer
than it did, so its answer is recorded. Only a retry it would need is refused once the limit has
passed, and the flow then pauses on the limit.
When the session cannot be found again (its process exited without an answer, its record could not
be read, or the daemon stopped before it recorded the session), the flow pauses saying that the
session was lost and its usage was not observed, and resuming the flow performs the step again with
a new session. A session lost during its retry is the exception to that last part: its record kept
what the turn before the retry spent, and the pause records that usage. When reattaching a session
fails outright, or its launch did not record the agent and model to record its answer with, the
daemon stops the session before the flow pauses, so it does not go on running unobserved. A session
whose record could not be read cannot be stopped this way, because nothing says which process is
its own. At start, the
daemon also stops every recorded session that no running flow is waiting on.

The daemon removes from a session's environment the variables an enclosing Claude Code session sets
for itself, so a daemon started from inside Claude Code runs its sessions as independent ones. It
also removes `CLAUDE_CODE_SAFE_MODE` and `CLAUDE_CODE_SIMPLE`, which would make Claude Code skip the
session's tool guard.

### How a session talks back

A session reaches the daemon that spawned it through `coa`, as a principal of its own. Every process
of a session, the retry's included, starts with these variables set:

| Variable | Holds |
| --- | --- |
| `COANDA_SESSION_TOKEN` | A session token issued for this flow and this launch of the judgment |
| `COANDA_HOME` | The node home of the daemon that spawned the session |
| `COANDA_SESSION_HOME` | The same node home, naming the one daemon the session token belongs to |
| `COANDA_PROJECT` | The flow's project, which `coa` uses when no `--project` is given |

The directory that holds the `coa` executable running the daemon goes first on the session's
`PATH`, so the session runs `coa` by name. When `COANDA_SESSION_TOKEN` is set and `coa` is reaching
the node home `COANDA_SESSION_HOME` names, it presents that token instead of the daemon token, and it
never starts a daemon: if none answers, it says that the daemon which started the session is not
running. Pointed at any other node home (a session that sets `COANDA_HOME` to a scratch home, or
a test suite that starts its own daemon on a throwaway one), `coa` never sends the session token and
works as it does outside a session, so the token only ever reaches the daemon that issued it.

The token makes the call an Agent-role principal bound to its flow and its launch. Queries, parcel
moves, and drafting a definition are open to it in any project, as they are to any agent. The flow
recording entries (a step, a launch, the session a launch returned, an answer, the usage a session
reported, a result, a pause, an end) are open to it only on its own flow, and only while its launch
is the one the flow expects. It can never start or follow a flow directly. A parcel move it makes
can still start one, as any parcel move can: moving a parcel into a state its type binds to a
contour starts that contour's flow under the binding's principal, not the session's. The
token resolves until the launch's answer is recorded, its flow pauses, stops, or ends, or the
judgment launches again; after that it is refused like any token the daemon did not issue, even
once the flow is resumed. The daemon keeps only a hash of each token, in memory, so a token does not
survive a daemon restart.

When a restarted daemon reattaches a session, it issues that session a new token for the same
launch, and the session's retry, if it needs one, starts with the new token and the other variables
above. The process that was already running across the restart keeps the token the earlier daemon
issued, which the new daemon refuses, so that process's `coa` calls back to the daemon fail until
it exits. Its answer is still collected from the transcript.

The token governs what a session's calls may record. It does not confine the session's process,
which runs as your user and can read whatever you can; the tool guard below keeps the session's own
tool calls out of the node home and your Claude Code configuration.

### What a session's tools cannot reach

Every Claude Code session carries a tool guard: a pre-tool-use hook, run as `coa tool-guard`, that
Claude Code calls before every Read, Edit, Write, Glob, Grep, and Bash call. It refuses the call when
a path the call names is one of these directories, is inside one, or contains one:

- the node home, so its token file, database, log, clones, and other flows' work directories;
- the config directory of every credential profile in `node.json`;
- the default Claude Code config directory: the `CLAUDE_CONFIG_DIR` the daemon inherited, or
  `~/.claude` when it inherited none, whatever profile the session runs under;
- when the daemon inherited no `CLAUDE_CONFIG_DIR`, Claude Code's global config file `~/.claude.json`
  and its backup `~/.claude.json.backup`, which sit beside `~/.claude` rather than inside it and hold
  your account details, your user-scope MCP servers, and any API key stored there. With the variable
  set, that file lives inside the config directory and is covered by it.

Two directories inside those stay open: the session's own work directory, and the directory Claude
Code keeps for the session's own conversation (`projects/<encoded work directory>/<session id>` in
the config directory the session runs under, beside its transcript). The guard reads the file path of
a Read, Edit, or Write; the path of a Glob or Grep and the pattern of a Glob; and every word of a
Bash command that looks like a path, after expanding `~` and the session's variables as its shell
would (`$PWD` is the directory the shell is in at that point, and a variable the session does not define is
empty). A relative word in a command is judged against each directory a `cd` or `pushd` earlier in
the command may have moved the shell to, and against the directory the call runs in as well, unless
the word's command is joined to the `cd` by `&&`; the destination of a `cd` is judged too, and a bare
`cd` goes to your home directory, which holds the node home. A `cd`, or zsh's `chdir`, is recognised
behind shell keywords such as `then` or `{` and behind `builtin`, `command`, and `eval`; its options
are read as the shell reads them, with backslashes removed, so `cd -\P` is a bare `cd`. After a `popd`, or a `pushd` that
rotates the directory stack, a relative word is judged against every directory the shell has been in
during the command. Every word is judged both as written and with its backslashes removed, as the
shell reads it outside quotes. The guard refuses a call that depends on `$OLDPWD`, `cd -`, or `~-`,
since it cannot know the shell's previous directory; on a `~NAME` lookup, since it cannot know
another user's home or a zsh named directory; on zsh's two-argument `cd OLD NEW`, which replaces
part of the current directory's name; on a `cd +1` or `cd -1`, which zsh reads as a place on the
directory stack and bash as a directory's name; on a `cd -e` or another option only bash knows, which
zsh reads as a directory; and on a `cd` into a process substitution. A word such as `-x`, `+x`, or
any word after `--` is a `cd`'s directory, as zsh reads it. A redirection's words and a comment's are
not read as a `cd`'s directory, so `cd src 2>/dev/null` and `cd src # enter it` move the shell to
`src`. In a command holding a quote, a backslash, or `${`, the guard cannot tell them from
arguments, and the same is true anywhere of a number of more than one digit or a `{NAME}` written
right before a redirection, which bash reads as a descriptor; the guard reads such a `cd` both ways
and refuses it when the two readings move the shell to different places. A word or a Glob pattern
with braces is judged as each word its brace expansion gives, since a choice may climb, hold several
components, or be an absolute path; one that expands to more than 256 words is refused. A path with a
wildcard is judged by the highest directory it may reach: the part before its first wildcard, climbed
once for each `..` after it (and each `.*` or `.?`, which some shells match to `..`) beyond the
levels its other components descend, where `**` counts as descending none. On Windows it also reads a Git Bash drive path such as
`/c/Users/me` as `C:\Users\me`. It resolves symbolic links before it compares, and on macOS and
Windows it ignores case. A refused call reaches the session as an error saying why.

The guard also refuses a command that may change where a later `cd` goes in a way it does not
follow:

- one that may set `CDPATH` (or zsh's `cdpath`), the directories a `cd` searches for a relative
  directory;
- one that may turn on the option `AUTO_CD`, `CDABLE_VARS`, or `PUSHD_TO_HOME`, named in any case
  and with any underscores, or through the letters `J`, `T`, and `D` zsh's `set` and `setopt` take
  for them;
- an assignment to a variable whose name the shell expands, such as `${v}PATH=x`, or a zsh
  `${(P)v::=x}`, either of which may set `CDPATH` without naming it.

Only the shell sets its own variables and options, so a word that merely mentions one of those names
is allowed when it is the plain name or argument of a program the shell starts, in a command with no
`$` or backtick in any of its words: `grep -rn CDPATH src` and a `git commit -m` whose message names it
both run. The same word is refused before a command's name, as an argument of a shell builtin such as
`export`, `setopt`, or `echo`, behind `eval`, right after a `(` (which may open an arithmetic
`(( CDPATH = 1 ))` or `$(( … ))` that assigns it), or anywhere on a line that defines a function or
an alias. When the daemon
itself runs with a non-empty `CDPATH`, every `cd` or `pushd` to a relative directory the shell would
look up in it is refused, which is any relative directory not starting with `./` or `../`: write
`cd ./src` rather than `cd src`, or unset `CDPATH` in the environment the daemon starts from.

The guard keeps an agent's ordinary tool calls away from those files. It is not a sandbox. A program
a command starts reads what it likes (`git` reads the clone, and `coa` reads `daemon.json` for the
daemon's port), and a command written to evade the guard can. The hook is passed inline on Claude
Code's command line with hooks explicitly left on, so your own settings files cannot switch it off,
and a guard that cannot run blocks the call rather than letting it through. Managed settings that
disable hooks still do, since they outrank everything else.

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
