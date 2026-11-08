using System.Text.Json;
using Coanda.Core;
using Coanda.Daemon;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Executors.ClaudeCode;

namespace Coanda.Tests.Daemon.Executors;

/// <summary>
/// Drives the tool guard's entry point as <c>coa tool-guard</c> runs it: the raw argument list the
/// adapter renders, and the JSON a pre-tool-use call delivers on standard input.
/// </summary>
public sealed class ToolGuardTests : IDisposable
{
    private const string Project = "alpha";
    private const string SessionId = "3f6c1b2a-0000-4000-8000-000000000001";

    private readonly string root = Path.Combine(Path.GetTempPath(), "coanda-guard-tests", Guid.NewGuid().ToString("N"));
    private readonly NodeHome home;
    private readonly string work;
    private readonly string defaultConfig;
    private readonly CredentialProfile directoryProfile;
    private readonly CredentialProfile otherProfile;
    private readonly CredentialProfile helperProfile;

    public ToolGuardTests()
    {
        home = new NodeHome(Path.Combine(root, "home"));
        work = home.FlowRunWorkPath(Project, Ids.New());
        defaultConfig = Path.Combine(root, "user", ".claude");
        directoryProfile = new CredentialProfile("claude-code", "work", Path.Combine(root, "profiles", "work"), null);
        otherProfile = new CredentialProfile("claude-code", "other", Path.Combine(home.Path, "profiles", "other"), null);
        helperProfile = new CredentialProfile("claude-code", "ci", null, "print-key");
        foreach (string directory in new[] { work, defaultConfig, directoryProfile.ConfigDirectory!, otherProfile.ConfigDirectory!, Path.Combine(work, "src") })
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(home.DaemonInfoPath, """{"port":1}""");
        File.WriteAllText(Path.Combine(work, "src", "a.cs"), "class A;");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // Refused.

    [Fact]
    public void A_read_of_daemon_json_by_its_absolute_path_is_refused_as_in_the_node_home()
    {
        ToolGuardOutcome outcome = Guard(Call("Read", new { file_path = home.DaemonInfoPath }));

        RefusedInNodeHome(outcome);
        // the reason never holds a file's contents
        Assert.DoesNotContain("port", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void On_windows_and_macos_the_same_read_with_its_letters_in_another_case_is_refused()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        RefusedInNodeHome(Guard(Call("Read", new { file_path = home.DaemonInfoPath.ToUpperInvariant() })));
        RefusedInNodeHome(Guard(Call("Read", new { file_path = home.DaemonInfoPath.ToLowerInvariant() })));
    }

    [Theory]
    [InlineData("cat $COANDA_HOME/daemon.json")]
    [InlineData("cat \"$COANDA_HOME/daemon.json\"")]
    [InlineData("cat '${COANDA_HOME}'/daemon.json")]
    [InlineData("cat \"${COANDA_HOME}/daemon.json\" | head -c 10")]
    public void A_shell_command_printing_daemon_json_through_the_node_home_variable_is_refused(string command) =>
        RefusedInNodeHome(Guard(Call("Bash", new { command }), environment: Environment()));

    [Fact]
    public void A_shell_command_printing_daemon_json_through_a_path_climbing_out_of_the_working_directory_is_refused() =>
        RefusedInNodeHome(Guard(Call("Bash", new { command = "cat ../../../daemon.json" }, cwd: work)));

    [Fact]
    public void A_shell_command_printing_a_file_in_a_profiles_config_directory_through_its_variable_is_refused()
    {
        IReadOnlyDictionary<string, string> environment = Environment(("CLAUDE_CONFIG_DIR", directoryProfile.ConfigDirectory!));

        RefusedInConfig(Guard(Call("Bash", new { command = "cat $CLAUDE_CONFIG_DIR/.credentials.json" }), directoryProfile, environment));
    }

    [Fact]
    public void A_grep_whose_path_is_the_node_home_is_refused() =>
        RefusedInNodeHome(Guard(Call("Grep", new { pattern = "token", path = home.Path })));

    [Fact]
    public void A_grep_whose_path_contains_the_node_home_is_refused() =>
        RefusedInNodeHome(Guard(Call("Grep", new { pattern = "token", path = root })));

    [Fact]
    public void A_glob_whose_pattern_is_an_absolute_path_under_the_node_home_is_refused() =>
        RefusedInNodeHome(Guard(Call("Glob", new { pattern = Path.Combine(home.Path, "**", "*.json") })));

    [Fact]
    public void A_glob_whose_pattern_holds_a_wildcard_above_the_node_home_is_refused() =>
        RefusedInNodeHome(Guard(Call("Glob", new { pattern = Path.Combine(root, "*", "daemon.json") })));

    [Fact]
    public void A_write_into_another_flow_runs_work_directory_is_refused() =>
        RefusedInNodeHome(Guard(Call("Write", new { file_path = Path.Combine(home.FlowRunWorkPath(Project, Ids.New()), "notes.md"), content = "x" })));

    [Fact]
    public void A_write_into_a_profiles_config_directory_is_refused() =>
        RefusedInConfig(Guard(Call("Write", new { file_path = Path.Combine(directoryProfile.ConfigDirectory!, "settings.json"), content = "{}" })));

    [Fact]
    public void A_write_into_a_profiles_config_directory_kept_in_the_node_home_is_refused() =>
        RefusedInNodeHome(Guard(Call("Write", new { file_path = Path.Combine(otherProfile.ConfigDirectory!, "settings.json"), content = "{}" })));

    [Fact]
    public void A_read_in_the_default_config_directory_is_refused_when_the_spawn_carries_no_profile() =>
        RefusedInConfig(Guard(Call("Read", new { file_path = Path.Combine(defaultConfig, "settings.json") }), profile: null));

    [Fact]
    public void A_read_in_the_default_config_directory_is_refused_when_the_spawn_carries_an_api_key_helper_profile() =>
        RefusedInConfig(Guard(Call("Read", new { file_path = Path.Combine(defaultConfig, "settings.json") }), helperProfile));

    [Fact]
    public void A_read_in_the_default_config_directory_is_refused_when_the_spawn_carries_a_profile_with_its_own_config_directory() =>
        RefusedInConfig(Guard(Call("Read", new { file_path = Path.Combine(defaultConfig, "settings.json") }), directoryProfile));

    [Fact]
    public void A_read_in_the_config_directory_the_session_runs_under_outside_its_conversation_is_refused() =>
        RefusedInConfig(Guard(Call("Read", new { file_path = Path.Combine(directoryProfile.ConfigDirectory!, ".credentials.json") }), directoryProfile));

    [Fact]
    public void A_read_in_another_sessions_conversation_directory_is_refused()
    {
        string encoded = Path.Combine(defaultConfig, "projects", "-encoded-work");
        string other = Path.Combine(encoded, "another-session", "tool-results", "x.txt");

        RefusedInConfig(Guard(Call("Read", new { file_path = other }, transcript: Path.Combine(encoded, SessionId + ".jsonl"))));
    }

    [Fact]
    public void A_transcript_path_outside_the_config_directory_the_session_runs_under_opens_no_conversation_directory()
    {
        string encoded = Path.Combine(directoryProfile.ConfigDirectory!, "projects", "-encoded-work");
        string file = Path.Combine(encoded, SessionId, "tool-results", "x.txt");

        RefusedInConfig(Guard(Call("Read", new { file_path = file }, transcript: Path.Combine(encoded, SessionId + ".jsonl")), profile: null));
    }

    [Fact]
    public void A_shell_word_naming_the_parent_of_the_work_directory_is_refused() =>
        RefusedInNodeHome(Guard(Call("Bash", new { command = "ls .." })));

    [Fact]
    public void A_shell_word_whose_part_after_an_equals_sign_reaches_the_node_home_is_refused() =>
        RefusedInNodeHome(Guard(Call("Bash", new { command = $"tool --input={home.DaemonInfoPath}" })));

    [Fact]
    public void A_shell_word_beginning_with_a_tilde_is_judged_under_the_home_directory_the_shell_expands_it_to()
    {
        // HOME first, as Git Bash expands ~ on Windows too; USERPROFILE only when HOME is unset.
        RefusedInConfig(Guard(Call("Bash", new { command = "cat ~/.claude/settings.json" }), environment: Environment(("HOME", Path.Combine(root, "user")), ("USERPROFILE", Path.Combine(root, "elsewhere")))));
        if (OperatingSystem.IsWindows())
        {
            RefusedInConfig(Guard(Call("Bash", new { command = "cat ~/.claude/settings.json" }), environment: Environment(("USERPROFILE", Path.Combine(root, "user")))));
        }
    }

    [Fact]
    public void A_path_reached_through_a_symbolic_link_in_the_work_directory_is_judged_where_it_leads()
    {
        string link = Path.Combine(work, "escape");
        try
        {
            Directory.CreateSymbolicLink(link, home.Path);
        }
        catch (Exception unsupported) when (unsupported is IOException or UnauthorizedAccessException)
        {
            // Windows without the privilege to create links.
            return;
        }

        RefusedInNodeHome(Guard(Call("Read", new { file_path = Path.Combine(link, "daemon.json") })));
        RefusedInNodeHome(Guard(Call("Bash", new { command = "cat escape/daemon.json" })));
    }

    [Fact]
    public void A_shell_word_climbing_from_pwd_is_judged_from_the_directory_the_call_runs_in_not_the_inherited_pwd()
    {
        // The hook inherits the PWD of whatever shell started the daemon; the session's shell sets its own.
        IReadOnlyDictionary<string, string> environment = Environment(("PWD", Path.Combine(root, "elsewhere", "a", "b")));

        RefusedInNodeHome(Guard(Call("Bash", new { command = "ls $PWD/.." }), environment: environment));
        RefusedInNodeHome(Guard(Call("Bash", new { command = $"cat \"${{PWD}}/../{Ids.New()}/notes.md\"" }), environment: environment));
        RefusedInNodeHome(Guard(Call("Bash", new { command = "(cd src) && ls $PWD/.." }), environment: environment));
    }

    [Fact]
    public void A_call_depending_on_the_shells_previous_directory_is_refused()
    {
        RefusedUnknowable(Guard(Call("Bash", new { command = "cat $OLDPWD/daemon.json" }), environment: Environment(("OLDPWD", Path.Combine(root, "elsewhere")))));
        RefusedUnknowable(Guard(Call("Bash", new { command = "cd - && cat daemon.json" })));
    }

    [Fact]
    public void A_variable_the_session_does_not_define_expands_to_nothing_as_the_shell_expands_it()
    {
        // On Windows the path is written as Git Bash names a drive, since a C: right after the name would lengthen it.
        string path = OperatingSystem.IsWindows()
            ? "/" + home.DaemonInfoPath[..1].ToLowerInvariant() + home.DaemonInfoPath[2..].Replace('\\', '/')
            : home.DaemonInfoPath;

        RefusedInNodeHome(Guard(Call("Bash", new { command = $"cat $UNDEFINED_PREFIX{path}" })));
    }

    [Fact]
    public void A_bare_cd_is_judged_as_the_home_directory_it_goes_to() =>
        RefusedInConfig(Guard(Call("Bash", new { command = "cd && cat .claude/settings.json" }), environment: Environment(("HOME", Path.Combine(root, "user")))));

    [Fact]
    public void A_relative_word_after_a_cd_is_judged_from_where_the_cd_may_have_moved_the_shell()
    {
        string start = Path.Combine(root, "elsewhere", "a", "b");
        string other = Path.Combine(root, "other", "x");
        Directory.CreateDirectory(start);
        Directory.CreateDirectory(other);

        Allowed(Guard(Call("Bash", new { command = "cat ../../home/daemon.json" }, cwd: start)));
        RefusedInNodeHome(Guard(Call("Bash", new { command = $"cd {other} && cat ../../home/daemon.json" }, cwd: start)));
        RefusedInNodeHome(Guard(Call("Bash", new { command = $"FOO=1 pushd -q {other}; cat ../../home/daemon.json" }, cwd: start)));
        Allowed(Guard(Call("Bash", new { command = $"cd {other} && cd {start} && cat ../../home/daemon.json" }, cwd: start)));
    }

    [Fact]
    public void A_directory_change_inside_a_subshell_ends_with_the_subshell() =>
        RefusedInNodeHome(Guard(Call("Bash", new { command = "(cd src && ls) && cat ../notes.md" })));

    [Fact]
    public void A_cd_not_joined_by_and_to_the_next_command_leaves_it_judged_from_where_the_shell_was_too()
    {
        Directory.CreateDirectory(Path.Combine(work, "a", "b", "c"));

        RefusedInNodeHome(Guard(Call("Bash", new { command = "cd a/b/c | cat ../../../daemon.json" })));
        RefusedInNodeHome(Guard(Call("Bash", new { command = "cd missing/b/c; cat ../../../daemon.json" })));
        RefusedInNodeHome(Guard(Call("Bash", new { command = "cd a/b/c & cat ../../../daemon.json" })));
        RefusedInNodeHome(Guard(Call("Bash", new { command = "cd a/b/c && ls || cat ../../../daemon.json" })));
        RefusedInNodeHome(Guard(Call("Bash", new { command = "true || cd a/b/c && cat ../../../daemon.json" })));
        RefusedInNodeHome(Guard(Call("Bash", new { command = "cd a/b/c && ls\ncat ../../../daemon.json" })));
        Allowed(Guard(Call("Bash", new { command = "cd a/b/c && ls ../../.. && cat ../x | grep ../../y" })));
        Allowed(Guard(Call("Bash", new { command = "true && cd a/b/c && (ls ../..; cat ../x)" })));
    }

    [Fact]
    public void A_popd_or_a_pushd_rotating_the_stack_leaves_the_next_command_judged_from_every_directory_the_shell_has_been_in()
    {
        Directory.CreateDirectory(Path.Combine(work, "src", "x", "y", "z"));

        RefusedInNodeHome(Guard(Call("Bash", new { command = "mkdir -p src/x/y/z && pushd src/x/y/z && popd && cat ../../../daemon.json" })));
        RefusedInNodeHome(Guard(Call("Bash", new { command = "pushd src/x/y/z && pushd +1 && cat ../../../daemon.json" })));
        RefusedInNodeHome(Guard(Call("Bash", new { command = "pushd src/x/y/z && pushd && cat ../../../daemon.json" })));
        Allowed(Guard(Call("Bash", new { command = "pushd src/x/y/z && ls ../.. && popd && ls src" })));
    }

    [Theory]
    [InlineData("cd src/x/y/z && builtin cd ../../.. && cat ../../../daemon.json")]
    [InlineData("cd src/x/y/z && command cd ../../.. && cat ../../../daemon.json")]
    [InlineData("cd src/x/y/z && command -p cd ../../.. && cat ../../../daemon.json")]
    [InlineData("cd src/x/y/z && \\cd ../../.. && cat ../../../daemon.json")]
    public void A_cd_behind_a_wrapper_or_a_backslash_is_still_read_as_a_cd(string command)
    {
        Directory.CreateDirectory(Path.Combine(work, "src", "x", "y", "z"));

        RefusedInNodeHome(Guard(Call("Bash", new { command })));
    }

    [Theory]
    [InlineData("if true; then cd; fi; cat .claude/settings.json")]
    [InlineData("{ cd; cat .claude/settings.json; }")]
    [InlineData("! cd && cat .claude/settings.json")]
    [InlineData("for d in a; do cd; done; cat .claude/settings.json")]
    [InlineData("builtin cd && cat .claude/settings.json")]
    [InlineData("eval \"cd; cat .claude/settings.json\"")]
    public void A_bare_cd_behind_a_shell_keyword_is_judged_as_the_home_directory_it_goes_to(string command) =>
        RefusedInConfig(Guard(Call("Bash", new { command }), environment: Environment(("HOME", Path.Combine(root, "user")))));

    [Fact]
    public void Command_v_cd_only_prints_and_is_not_read_as_a_cd() =>
        Allowed(Guard(Call("Bash", new { command = "command -v cd && cat .claude/settings.json" }), environment: Environment(("HOME", Path.Combine(root, "user")))));

    [Theory]
    [InlineData("cd alpha . && cat daemon.json")]
    [InlineData("cd alpha b>x && cat daemon.json")]
    [InlineData("cd alpha 12>x && cat daemon.json")]
    [InlineData("cd alpha 1&>x && cat daemon.json")]
    [InlineData("cd alpha \\>&x && cat daemon.json")]
    [InlineData("cd alpha >(true) && cat daemon.json")]
    [InlineData("cd alpha \"#x\" && cat daemon.json")]
    [InlineData("cd alpha \" #x\" && cat daemon.json")]
    [InlineData("cd alpha \" >x\" && cat daemon.json")]
    [InlineData("cd alpha '\"' \" >x\" && cat daemon.json")]
    [InlineData("cd alpha ${x:- #} && cat daemon.json")]
    [InlineData("ls (#i)x; cd alpha . && cat daemon.json")]
    [InlineData("cd alpha \\#x && cat daemon.json")]
    [InlineData("pushd alpha . && cat daemon.json")]
    [InlineData("pushd -q alpha . && cat daemon.json")]
    [InlineData("pushd -- alpha . && cat daemon.json")]
    [InlineData("pushd alpha 12>x && cat daemon.json")]
    [InlineData("pushd +1 . && cat daemon.json")]
    [InlineData("builtin pushd alpha . && cat daemon.json")]
    public void A_cd_or_pushd_given_two_directories_which_zsh_reads_as_a_replacement_is_refused(string command) =>
        RefusedUnknowable(Guard(Call("Bash", new { command })));

    [Theory]
    [InlineData("CDPATH=/nonexistent:../../.. cd alpha && cat daemon.json")]
    [InlineData("export CDPATH=/nonexistent:../../..; cd alpha; cat daemon.json")]
    [InlineData("export \"CDPATH=/x:../../..\"; cd alpha; cat daemon.json")]
    [InlineData("cdpath=(../../..) && cd alpha && cat daemon.json")]
    [InlineData("typeset -T Cd\\PATH x; cd alpha")]
    [InlineData("v=CD; export ${v}PATH=../../..; cd alpha; cat daemon.json")]
    [InlineData("v=CDPATH; : ${(P)v::=../../..}; cd alpha; cat daemon.json")]
    [InlineData("setopt pushdtohome; pushd; cat .claude/settings.json")]
    [InlineData("setopt PUSHD_TO_HOME && pushd && cat .claude/settings.json")]
    [InlineData("set -o pushd_to_home; pushd; cat .claude/settings.json")]
    [InlineData("set -D; pushd; cat .claude/settings.json")]
    [InlineData("setopt -D && pushd && cat .claude/settings.json")]
    [InlineData("setopt cdablevars; h=../../..; cd h; cat daemon.json")]
    [InlineData("set -T; cd h; cat daemon.json")]
    [InlineData("setopt auto_cd; ../../..; cat daemon.json")]
    [InlineData("shopt -s cdable_vars; cd h; cat daemon.json")]
    public void A_command_that_may_change_where_a_cd_goes_is_refused(string command) =>
        RefusedUnknowable(Guard(Call("Bash", new { command }), environment: Environment(("HOME", Path.Combine(root, "user")))));

    [Theory]
    [InlineData("grep x ${CDPATH:=../../..}; cd alpha; cat daemon.json")]
    [InlineData("git log $((CDPATH=1)); cd alpha")]
    [InlineData("(( CDPATH = 1 )); cd alpha")]
    [InlineData("git log $(( CDPATH = 1 )); cd alpha")]
    [InlineData("echo $(( CDPATH += 1 )); cd alpha")]
    [InlineData("git log $[ CDPATH = 1 ]; cd alpha")]
    [InlineData("git log ${a[ CDPATH = 1 ]}; cd alpha")]
    [InlineData("ls *(e: CDPATH = 1 :); cd alpha")]
    [InlineData("echo CDPATH=../../.. >x; cd alpha")]
    [InlineData("eval grep x\\; setopt autocd; ../../..; cat daemon.json")]
    [InlineData("f() { eval \"$@\"; }; f setopt autocd; ../../..; cat daemon.json")]
    [InlineData("function f { eval \"$@\"; }; f setopt autocd; ../../..; cat daemon.json")]
    [InlineData("alias g=eval; g setopt auto_cd; ../../..; cat daemon.json")]
    [InlineData("$x setopt autocd; ../../..; cat daemon.json")]
    [InlineData("\"setopt\" -J; ../../..; cat daemon.json")]
    public void A_mention_a_shell_may_act_on_is_still_refused(string command) =>
        RefusedUnknowable(Guard(Call("Bash", new { command }), environment: Environment(("HOME", Path.Combine(root, "user")))));

    [Theory]
    [InlineData("grep -rn CDPATH src")]
    [InlineData("rg -n 'AUTO_CD|cdable_vars' src")]
    [InlineData("git log --grep autocd")]
    [InlineData("git commit -m \"Refuse a command that may change where a cd goes through CDPATH or a shell option\"")]
    [InlineData("cat <<EOF > notes.md\nAUTO_CD and PUSHD_TO_HOME\nEOF")]
    [InlineData("dotnet test --filter set -T")]
    [InlineData("(( n = 1 )); grep -rn CDPATH src")]
    public void A_program_s_argument_that_only_mentions_cdpath_or_an_option_is_allowed(string command) =>
        Allowed(Guard(Call("Bash", new { command })));

    [Theory]
    [InlineData("cd -\\P && cat .claude/settings.json")]
    [InlineData("cd -\\- && cat .claude/settings.json")]
    [InlineData("cd -\\q && cat .claude/settings.json")]
    [InlineData("cd \\-L && cat .claude/settings.json")]
    [InlineData("cd -\\P -\\- && cat .claude/settings.json")]
    public void A_cd_option_written_with_a_backslash_is_still_read_as_an_option(string command) =>
        RefusedInConfig(Guard(Call("Bash", new { command }), environment: Environment(("HOME", Path.Combine(root, "user")))));

    [Theory]
    [InlineData("cd \\- && cat daemon.json")]
    [InlineData("cd -\\1 && cat ../../../daemon.json")]
    [InlineData("cd -\\e && cat .claude/settings.json")]
    [InlineData("pushd -\\- +1 && cat ../../../daemon.json")]
    public void A_cd_word_written_with_a_backslash_is_read_as_the_shell_reads_it(string command) =>
        RefusedUnknowable(Guard(Call("Bash", new { command }), environment: Environment(("HOME", Path.Combine(root, "user")))));

    [Fact]
    public void A_pushd_to_a_stack_place_written_with_a_backslash_rotates_the_stack()
    {
        Directory.CreateDirectory(Path.Combine(work, "src", "x", "y", "z"));

        RefusedInNodeHome(Guard(Call("Bash", new { command = "pushd src/x/y/z && pushd +\\1 && cat ../../../daemon.json" })));
    }

    [Theory]
    [InlineData("chdir src && cat ../../../../daemon.json")]
    [InlineData("builtin chdir src && cat ../../../../daemon.json")]
    public void Zsh_s_chdir_is_read_as_a_cd(string command)
    {
        Directory.CreateDirectory(Path.Combine(work, "src"));

        RefusedInNodeHome(Guard(Call("Bash", new { command })));
    }

    [Theory]
    [InlineData("cd alpha && cat daemon.json")]
    [InlineData("pushd alpha && cat daemon.json")]
    [InlineData("cd src")]
    public void With_an_inherited_cdpath_a_cd_the_shell_searches_it_for_is_refused(string command) =>
        RefusedUnknowable(Guard(Call("Bash", new { command }), environment: Environment(("CDPATH", Path.Combine(root, "home", "flow-runs")))));

    [Theory]
    [InlineData("cd ./src && cat a.cs")]
    [InlineData("cd . && cat src/a.cs")]
    [InlineData("cd && cat src/a.cs")]
    [InlineData("cat src/a.cs")]
    public void With_an_inherited_cdpath_a_cd_the_shell_does_not_search_it_for_is_judged_as_before(string command) =>
        Allowed(Guard(Call("Bash", new { command }), environment: Environment(("CDPATH", Path.Combine(root, "elsewhere")), ("HOME", work))));

    [Theory]
    [InlineData("cd src && cat a.cs")]
    [InlineData("set -euo pipefail; cd src && cat a.cs")]
    [InlineData("set -x && cd src")]
    [InlineData("A=1 cd src && cat a.cs")]
    public void Without_a_cdpath_or_an_option_a_cd_into_the_work_directory_is_allowed(string command) =>
        Allowed(Guard(Call("Bash", new { command })));

    [Theory]
    [InlineData("cd src >/dev/null && cat a.cs")]
    [InlineData("cd src 2>/dev/null || true")]
    [InlineData("cd src 2>&1 && cat a.cs")]
    [InlineData("cd src >>log.txt 2>>err.txt && cat a.cs")]
    [InlineData("cd src </dev/null && cat a.cs")]
    [InlineData("cd src # enter the source directory\ncat a.cs")]
    [InlineData("cd src # one; two && three\ncat a.cs")]
    [InlineData("cd src >#log && cat a.cs")]
    [InlineData("cd src 2>&1 >&2 <&0 && cat a.cs")]
    [InlineData("cd src &>/dev/null && cat a.cs")]
    [InlineData("cd src >|log && cat a.cs")]
    public void A_redirection_or_a_comment_after_a_cd_is_not_a_second_directory(string command) =>
        Allowed(Guard(Call("Bash", new { command })));

    [Theory]
    [InlineData("cd src/x/y 2>/dev/null && cat ../../../../../../daemon.json")]
    [InlineData("cd >/dev/null src/x/y && cat ../../../../../../daemon.json")]
    [InlineData("2>/dev/null cd src/x/y && cat ../../../../../../daemon.json")]
    [InlineData("cd 2>&1 src/x/y && cat ../../../../../../daemon.json")]
    [InlineData("cd >&2 src/x/y && cat ../../../../../../daemon.json")]
    [InlineData("cd <&0 src/x/y && cat ../../../../../../daemon.json")]
    [InlineData("cd >& /dev/null src/x/y && cat ../../../../../../daemon.json")]
    [InlineData("cd &>/dev/null src/x/y && cat ../../../../../../daemon.json")]
    [InlineData("cd &>>log src/x/y && cat ../../../../../../daemon.json")]
    [InlineData("cd >|log src/x/y && cat ../../../../../../daemon.json")]
    public void A_cd_with_a_redirection_still_moves_the_shell_to_its_directory(string command) =>
        RefusedInNodeHome(Guard(Call("Bash", new { command })));

    [Theory]
    [InlineData("cd >\"/dev/null\" && cat .claude/settings.json")]
    [InlineData("cd #\"x\"\ncat .claude/settings.json")]
    [InlineData("cd {fd}>/dev/null && cat .claude/settings.json")]
    [InlineData("cd 12>/dev/null && cat .claude/settings.json")]
    [InlineData("cd 3<\"x\" && cat .claude/settings.json")]
    [InlineData("12>/dev/null cd ../../.. && cat daemon.json")]
    [InlineData("{fd}>/dev/null cd ../../.. && cat daemon.json")]
    [InlineData(">\"/dev/null\" cd ../../.. && cat daemon.json")]
    [InlineData("cd <(true) && cat ../../Users/x/.claude/settings.json")]
    [InlineData("cd +1 && cat ../../../daemon.json")]
    [InlineData("pushd -- +1 && cat ../../../daemon.json")]
    [InlineData("cd -e && cat .claude/settings.json")]
    [InlineData("cd -P@ && cat .claude/settings.json")]
    public void A_cd_whose_directory_may_be_a_redirection_a_comment_or_a_stack_place_is_refused(string command) =>
        RefusedUnknowable(Guard(Call("Bash", new { command }), environment: Environment(("HOME", Path.Combine(root, "user")))));

    [Theory]
    [InlineData("cd -- -x && cat ../../../../daemon.json")]
    [InlineData("cd -x && cat ../../../../daemon.json")]
    [InlineData("cd +x && cat ../../../../daemon.json")]
    [InlineData("cd -P -- -x && cat ../../../../daemon.json")]
    [InlineData("cd -Px && cat ../../../../daemon.json")]
    public void A_cd_to_a_directory_named_like_an_option_moves_the_shell_into_it(string command)
    {
        foreach (string name in new[] { "-x", "+x", "-Px" })
        {
            Directory.CreateDirectory(Path.Combine(work, name));
        }

        RefusedInNodeHome(Guard(Call("Bash", new { command }), environment: Environment(("HOME", Path.Combine(root, "user")))));
    }

    [Theory]
    [InlineData("cd -P src && cat a.cs")]
    [InlineData("cd -- src && cat a.cs")]
    [InlineData("cd -L -- src 2>/dev/null && cat a.cs")]
    public void A_cd_with_options_still_moves_the_shell_to_its_directory(string command) =>
        Allowed(Guard(Call("Bash", new { command })));

    [Fact]
    public void A_cd_after_a_zsh_glob_flag_is_still_read_as_a_cd() =>
        RefusedInConfig(Guard(Call("Bash", new { command = "ls (#i)x; cd && cat .claude/settings.json" }), environment: Environment(("HOME", Path.Combine(root, "user")))));

    [Fact]
    public void An_escaped_redirection_before_an_ampersand_still_ends_the_command() =>
        RefusedInConfig(Guard(Call("Bash", new { command = "echo x\\>& cd && cat .claude/settings.json" }), environment: Environment(("HOME", Path.Combine(root, "user")))));

    [Fact]
    public void A_bare_cd_followed_by_a_comment_goes_to_the_home_directory() =>
        RefusedInConfig(Guard(Call("Bash", new { command = "cd # go home\ncat .claude/settings.json" }), environment: Environment(("HOME", Path.Combine(root, "user")))));

    [Theory]
    [InlineData("cat ~/ho\\me/daemon.json")]
    [InlineData("cat ~/\\home/daemon.json")]
    [InlineData("cat ~/ho\\\nme/daemon.json")]
    public void A_shell_word_with_backslashes_is_judged_as_the_shell_reads_it(string command) =>
        RefusedInNodeHome(Guard(Call("Bash", new { command }), environment: Environment(("HOME", root))));

    [Fact]
    public void A_tilde_naming_a_user_or_the_previous_directory_is_refused_and_one_naming_the_current_directory_is_judged_there()
    {
        RefusedUnknowable(Guard(Call("Bash", new { command = "cat ~someone/.coanda/daemon.json" })));
        RefusedUnknowable(Guard(Call("Read", new { file_path = "~someone/.coanda/daemon.json" })));
        RefusedUnknowable(Guard(Call("Bash", new { command = "cat ~-/daemon.json" })));
        RefusedInNodeHome(Guard(Call("Bash", new { command = "ls ~+/.." })));
        Allowed(Guard(Call("Bash", new { command = "ls ~+/src" })));
    }

    [Fact]
    public void With_no_inherited_config_directory_the_global_config_file_beside_the_default_one_is_refused()
    {
        string user = Path.Combine(root, "user");
        File.WriteAllText(Path.Combine(user, ".claude.json"), "{}");
        // HOME for the shell's ~, USERPROFILE for where the CLI keeps its global config on Windows.
        IReadOnlyDictionary<string, string> environment = Environment(("HOME", user), ("USERPROFILE", user));
        environment = environment.Where(pair => pair.Key != "CLAUDE_CONFIG_DIR").ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (CredentialProfile? profile in new[] { null, directoryProfile, helperProfile })
        {
            IReadOnlyList<string> arguments = ClaudeCodeInvocation.Guard(home, work, profile, environment, [directoryProfile.ConfigDirectory!, otherProfile.ConfigDirectory!]).Arguments;

            RefusedInConfig(ToolGuard.Run(arguments, new StringReader(Call("Read", new { file_path = Path.Combine(user, ".claude.json") })), environment));
            RefusedInConfig(ToolGuard.Run(arguments, new StringReader(Call("Edit", new { file_path = Path.Combine(user, ".claude.json.backup") })), environment));
            RefusedInConfig(ToolGuard.Run(arguments, new StringReader(Call("Bash", new { command = "cat ~/.claude.json" })), environment));
        }

        // with CLAUDE_CONFIG_DIR set, the global config lives inside that directory
        Assert.Empty(ClaudeCodeInvocation.DefaultConfigFiles(Environment()));
    }

    [Theory]
    [InlineData("cat */../../../daemon.json")]
    [InlineData("cat src/*/../../../../daemon.json")]
    [InlineData("cat {..,src}/daemon.json")]
    [InlineData("cat .*/.*/.*/daemon.json")]
    [InlineData("ls .?")]
    public void A_shell_word_that_may_climb_after_a_wildcard_is_judged_by_the_highest_directory_it_may_reach(string command) =>
        RefusedInNodeHome(Guard(Call("Bash", new { command })));

    [Fact]
    public void A_glob_whose_pattern_may_climb_after_a_wildcard_is_judged_by_the_highest_directory_it_may_reach()
    {
        RefusedInNodeHome(Guard(Call("Glob", new { pattern = "*/../../../*.json" })));
        RefusedInNodeHome(Guard(Call("Glob", new { pattern = "*/../../../*.json", path = Path.Combine(work, "src") })));
    }

    [Theory]
    [InlineData("foo/.*bar", "foo/", 1)]
    [InlineData("s/.*//", "s/", 1)]
    [InlineData("https?://.*", "", 0)]
    [InlineData("*/../../x", "", 1)]
    [InlineData("a/{..,b}/.?/c", "a/", 2)]
    [InlineData("src/*.cs", "src/", 0)]
    [InlineData("src/a.cs", "src/a.cs", 0)]
    public void The_highest_directory_a_pattern_may_reach_counts_each_climb_beyond_its_descents(string text, string before, int climbs) => Assert.Equal(before + string.Concat(Enumerable.Repeat(".." + Path.DirectorySeparatorChar, climbs)), ShellWords.HighestReach(text));

    [Theory]
    [InlineData("cat {a/b/c/d,e}/../../../../daemon.json")]
    [InlineData("cat x/{a/b/c/d,}/../../../../daemon.json")]
    [InlineData("cd {a/b/c/d,e}/../../../.. && cat daemon.json")]
    [InlineData("cat {a,b/..}/../../daemon.json")]
    [InlineData("cat **/../daemon.json")]
    [InlineData("cat src/**/../../daemon.json")]
    [InlineData("cat $${a/b/c/d/e,d}/../../../../daemon.json")]
    public void A_shell_word_whose_brace_choices_or_globstar_change_how_deep_it_goes_is_judged_by_each_choice(string command) =>
        RefusedInNodeHome(Guard(Call("Bash", new { command })));

    [Fact]
    public void A_brace_choice_that_is_an_absolute_path_or_begins_with_a_tilde_is_judged_as_one()
    {
        RefusedInNodeHome(Guard(Call("Bash", new { command = $"cat {{x,{home.DaemonInfoPath}}}" })));
        RefusedInNodeHome(Guard(Call("Bash", new { command = "cat {x,~/home/daemon.json}" }), environment: Environment(("HOME", root))));
        RefusedInNodeHome(Guard(Call("Glob", new { pattern = $"{{src/*.cs,{home.Path}/*.json}}" })));
        RefusedInNodeHome(Guard(Call("Glob", new { pattern = "{a/b/c/d,e}/../../../../*.json" })));
    }

    [Fact]
    public void A_brace_expansion_of_more_words_than_the_guard_expands_is_refused() =>
        RefusedUnknowable(Guard(Call("Bash", new { command = "cat src/{a,b}{a,b}{a,b}{a,b}{a,b}{a,b}{a,b}{a,b}{a,b}.cs" })));

    [Fact]
    public void Ordinary_braces_inside_the_work_directory_stay_allowed() =>
        Allowed(Guard(Call("Bash", new { command = "cp src/a.cs{,.bak} && ls src/{a,b}.cs {src,.}/a.cs && ls src/{1..3}.cs ${PWD}/src && grep -E \"src/x{2,3}\" src" })));

    [Theory]
    [InlineData("src/a.cs", new[] { "src/a.cs" })]
    [InlineData("{a/b/c/d,e}/x", new[] { "a/b/c/d/x", "e/x", "{a/b/c/d,e}/x" })]
    [InlineData("a{,.bak}", new[] { "a", "a.bak", "a{,.bak}" })]
    [InlineData("{a,{b,c}}d", new[] { "ad", "bd", "cd", "{a,{b,c}}d" })]
    [InlineData("{a}{b,c}", new[] { "{a}b", "{a}c", "{a}{b,c}" })]
    [InlineData("${HOME}/{1..3}", new[] { "${HOME}/{1..3}" })]
    [InlineData("${A,B}", new[] { "$A", "$B", "${A,B}" })]
    [InlineData("{a,b", new[] { "{a,b" })]
    public void A_brace_expands_to_each_of_its_choices_and_the_text_as_quoted(string text, string[] expected) => Assert.Equal(expected, ShellWords.Braces(text));

    [Fact]
    public void A_brace_expansion_of_more_words_than_the_limit_gives_none() => Assert.Null(ShellWords.Braces(string.Concat(Enumerable.Repeat("{a,b}", 9))));

    [Theory]
    [InlineData("/c/Users/me/.coanda/daemon.json", @"C:\Users\me\.coanda\daemon.json")]
    [InlineData("/cygdrive/d/work", @"D:\work")]
    [InlineData("/c", @"C:\")]
    [InlineData("/c/", @"C:\")]
    [InlineData("/usr/bin", null)]
    [InlineData("/cygdrive", null)]
    [InlineData("c/Users", null)]
    public void A_git_bash_drive_path_is_read_as_the_windows_drive_path_it_names(string text, string? expected) => Assert.Equal(expected, GuardPath.ShellDrivePath(text));

    [Fact]
    public void On_windows_a_git_bash_drive_path_into_the_node_home_is_refused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string drive = home.DaemonInfoPath[..1].ToLowerInvariant();
        string shellPath = "/" + drive + home.DaemonInfoPath[2..].Replace('\\', '/');

        RefusedInNodeHome(Guard(Call("Bash", new { command = $"cat {shellPath}" })));
    }

    [Fact]
    public void On_windows_a_path_rooted_with_no_drive_is_resolved_on_the_drive_of_its_base_directory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A letter no drive answers to differs from the process's current drive and has no device to probe.
        HashSet<char> taken = [.. DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0]))];
        char letter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Last(c => !taken.Contains(c));
        string baseDirectory = $@"{letter}:\work";

        Assert.Equal($@"{letter}:\", GuardPath.Resolve(@"\", baseDirectory));
        Assert.Equal($@"{letter}:\Users\x", GuardPath.Resolve(@"\Users\x", baseDirectory));
    }

    [Fact]
    public void An_input_that_is_not_json_is_refused() =>
        RefusedUnreadable(Run(Arguments(), "this is not json"));

    [Theory]
    [InlineData("""{"hook_event_name":"PreToolUse","tool_input":{"file_path":"x"}}""")]
    [InlineData("""{"hook_event_name":"PreToolUse","tool_name":"","tool_input":{}}""")]
    [InlineData("""{"hook_event_name":"PostToolUse","tool_name":"Read","tool_input":{"file_path":"x"}}""")]
    [InlineData("""{"hook_event_name":"PreToolUse","tool_name":"Read","tool_input":{}}""")]
    [InlineData("""{"hook_event_name":"PreToolUse","tool_name":"Read","tool_input":{"file_path":7}}""")]
    [InlineData("""{"hook_event_name":"PreToolUse","tool_name":"NotebookEdit","tool_input":{"notebook_path":"x"}}""")]
    [InlineData("""["PreToolUse"]""")]
    public void A_json_input_that_is_not_a_pre_tool_use_call_the_guard_can_read_is_refused(string input) =>
        RefusedUnreadable(Run(Arguments(), input));

    [Fact]
    public void A_json_input_that_names_no_tool_is_refused() =>
        RefusedUnreadable(Run(Arguments(), """{"hook_event_name":"PreToolUse","tool_input":{"file_path":"x"}}"""));

    [Theory]
    [InlineData]
    [InlineData("tool-guard")]
    [InlineData("tool-guard", "--node-home")]
    [InlineData("tool-guard", "--work", "/w", "--session-config", "/c")]
    [InlineData("tool-guard", "--node-home", "relative", "--work", "/w", "--session-config", "/c")]
    [InlineData("tool-guard", "--node-home", "/h", "--node-home", "/h", "--work", "/w", "--session-config", "/c")]
    [InlineData("tool-guard", "--node-home", "/h", "--work", "/w", "--session-config", "/c", "--unknown", "/x")]
    [InlineData("guard", "--node-home", "/h", "--work", "/w", "--session-config", "/c")]
    public void A_malformed_argument_list_is_refused(params string[] arguments)
    {
        ToolGuardOutcome outcome = Run(arguments, Call("Read", new { file_path = Path.Combine(work, "src", "a.cs") }));

        Assert.Equal(ToolGuardOutcome.DenyCode, outcome.ExitCode);
        Assert.StartsWith("coa refused this call because the tool guard's arguments are malformed", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_while_the_guard_decides_is_a_refusal()
    {
        ToolGuardOutcome outcome = Guard(Call("Read", new { file_path = "bad\0path" }));

        Assert.Equal(ToolGuardOutcome.DenyCode, outcome.ExitCode);
        Assert.StartsWith("coa refused this call because the tool guard failed while deciding it", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_input_that_cannot_be_read_is_a_refusal() => Assert.Equal(ToolGuardOutcome.DenyCode, Run(Arguments(), new FailingReader()).ExitCode);

    // Allowed.

    [Fact]
    public void A_read_an_edit_and_a_grep_inside_the_work_directory_are_allowed()
    {
        string file = Path.Combine(work, "src", "a.cs");

        Allowed(Guard(Call("Read", new { file_path = file })));
        Allowed(Guard(Call("Read", new { file_path = "src/a.cs" })));
        Allowed(Guard(Call("Edit", new { file_path = file, old_string = "A", new_string = "B" })));
        Allowed(Guard(Call("Write", new { file_path = Path.Combine(work, ".gitignore"), content = "bin/" })));
        Allowed(Guard(Call("Grep", new { pattern = "class", path = Path.Combine(work, "src") })));
        Allowed(Guard(Call("Grep", new { pattern = "class" })));
        Allowed(Guard(Call("Glob", new { pattern = "**/*.cs" })));
        Allowed(Guard(Call("Glob", new { pattern = "*.cs", path = Path.Combine(work, "src") })));
    }

    [Fact]
    public void A_grep_whose_regular_expression_holds_a_path_separator_is_allowed() =>
        Allowed(Guard(Call("Grep", new { pattern = "../../daemon.json|/Users/.*/.coanda", path = "src" })));

    [Fact]
    public void A_grep_whose_path_is_dot_and_a_shell_word_dot_name_the_work_directory_itself_and_are_allowed()
    {
        Allowed(Guard(Call("Grep", new { pattern = "class", path = "." })));
        Allowed(Guard(Call("Bash", new { command = "ls -la ." })));
        Allowed(Guard(Call("Bash", new { command = "find ./ -name '*.cs'" })));
    }

    [Fact]
    public void A_read_in_the_sessions_own_conversation_directory_under_the_config_directory_it_runs_under_is_allowed()
    {
        string encoded = Path.Combine(directoryProfile.ConfigDirectory!, "projects", "-encoded-work");
        string file = Path.Combine(encoded, SessionId, "tool-results", "x.txt");

        Allowed(Guard(Call("Read", new { file_path = file }, transcript: Path.Combine(encoded, SessionId + ".jsonl")), directoryProfile));
    }

    [Fact]
    public void A_read_in_the_conversation_directory_under_the_default_config_directory_is_allowed_when_the_session_runs_under_it()
    {
        string encoded = Path.Combine(defaultConfig, "projects", "-encoded-work");
        string file = Path.Combine(encoded, SessionId, "tool-results", "x.txt");

        Allowed(Guard(Call("Read", new { file_path = file }, transcript: Path.Combine(encoded, SessionId + ".jsonl")), helperProfile));
    }

    [Fact]
    public void The_build_and_test_commands_are_allowed() =>
        Allowed(Guard(Call("Bash", new { command = "dotnet build --configuration Release && dotnet test --configuration Release --no-build" }), environment: Environment()));

    [Fact]
    public void A_git_commit_whose_message_comes_through_a_heredoc_holding_an_apostrophe_is_allowed() =>
        Allowed(Guard(Call("Bash", new
        {
            command = """
                git add -A && git commit -m "$(cat <<'EOF'
                Teach the guard the session's own directory

                It reads src/a.cs, and the work's done.
                EOF
                )"
                """,
        }), environment: Environment()));

    [Fact]
    public void Coa_with_no_path_argument_is_allowed_from_a_working_directory_under_the_node_home() =>
        Allowed(Guard(Call("Bash", new { command = "coa flow-run show 0192a3b4 && coa parcel list --project alpha" }, cwd: work), environment: Environment()));

    [Fact]
    public void Ordinary_commands_using_pwd_cd_and_wildcards_inside_the_work_directory_are_allowed()
    {
        IReadOnlyDictionary<string, string> environment = Environment(("PWD", root));

        Allowed(Guard(Call("Bash", new { command = "find \"$PWD\" -name '*.cs'" }), environment: environment));
        Allowed(Guard(Call("Bash", new { command = "cd src && cat a.cs && ls ../src" }), environment: environment));
        Allowed(Guard(Call("Bash", new { command = "cd src && ls $PWD/.." }), environment: environment));
        Allowed(Guard(Call("Bash", new { command = "ls src/*.cs ./src/a{,.bak}.cs && for i in {1..5}; do echo $i; done" }), environment: environment));
        Allowed(Guard(Call("Bash", new { command = "ls -d src/.git*" }), environment: environment));
        Allowed(Guard(Call("Bash", new { command = "grep -rn \"foo/.*bar\" src && sed 's/.*//' a.txt && rg \"https?://.*\" src" }), environment: environment));
    }

    [Fact]
    public void Paths_outside_every_protected_directory_are_allowed() =>
        Allowed(Guard(Call("Bash", new { command = "ls /usr/bin > /dev/null 2>&1; git log origin/main" })));

    [Fact]
    public void The_rendered_rules_round_trip_through_their_argument_list()
    {
        ToolGuardRules rules = Rules(directoryProfile, Environment());

        ToolGuardRules? parsed = ToolGuardRules.Parse(rules.Arguments, out string? problem);
        Assert.True(parsed is not null, problem);
        Assert.Equivalent(rules, parsed, strict: true);
        Assert.Equivalent(new[] { directoryProfile.ConfigDirectory, otherProfile.ConfigDirectory, defaultConfig }, rules.ProtectedConfigDirectories, strict: true);
    }

    private static void RefusedInNodeHome(ToolGuardOutcome outcome)
    {
        Assert.Equal(ToolGuardOutcome.DenyCode, outcome.ExitCode);
        Assert.Contains("is in the node home", outcome.Reason, StringComparison.Ordinal);
    }

    private static void RefusedInConfig(ToolGuardOutcome outcome)
    {
        Assert.Equal(ToolGuardOutcome.DenyCode, outcome.ExitCode);
        Assert.Contains("is in a Claude Code config directory", outcome.Reason, StringComparison.Ordinal);
    }

    private static void RefusedUnknowable(ToolGuardOutcome outcome)
    {
        Assert.Equal(ToolGuardOutcome.DenyCode, outcome.ExitCode);
        Assert.Contains("which the tool guard cannot know", outcome.Reason, StringComparison.Ordinal);
    }

    private static void RefusedUnreadable(ToolGuardOutcome outcome)
    {
        Assert.Equal(ToolGuardOutcome.DenyCode, outcome.ExitCode);
        Assert.StartsWith("coa refused this call because the tool guard could not read it", outcome.Reason, StringComparison.Ordinal);
    }

    private static void Allowed(ToolGuardOutcome outcome) =>
        Assert.True(outcome == ToolGuardOutcome.Allowed, outcome.Reason);

    private IReadOnlyDictionary<string, string> Environment(params (string Name, string Value)[] more)
    {
        var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            [NodeHome.EnvironmentVariable] = home.Path,
            ["CLAUDE_CONFIG_DIR"] = defaultConfig,
        };
        foreach ((string name, string value) in more)
        {
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>The rules the adapter renders for this session, the daemon having inherited <c>CLAUDE_CONFIG_DIR</c> as the default config directory.</summary>
    private ToolGuardRules Rules(CredentialProfile? profile, IReadOnlyDictionary<string, string> inherited) =>
        ClaudeCodeInvocation.Guard(
            home,
            work,
            profile,
            new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = inherited.TryGetValue("CLAUDE_CONFIG_DIR", out string? configured) ? configured : defaultConfig },
            [directoryProfile.ConfigDirectory!, otherProfile.ConfigDirectory!]);

    private IReadOnlyList<string> Arguments(CredentialProfile? profile = null) => Rules(profile, Environment()).Arguments;

    private ToolGuardOutcome Guard(string input, CredentialProfile? profile = null, IReadOnlyDictionary<string, string>? environment = null) =>
        ToolGuard.Run(Arguments(profile), new StringReader(input), environment ?? Environment());

    private ToolGuardOutcome Run(IReadOnlyList<string> arguments, string input) =>
        ToolGuard.Run(arguments, new StringReader(input), Environment());

    private ToolGuardOutcome Run(IReadOnlyList<string> arguments, TextReader input) =>
        ToolGuard.Run(arguments, input, Environment());

    /// <summary>The JSON Claude Code delivers to a pre-tool-use hook for a call to <paramref name="tool"/>.</summary>
    private string Call(string tool, object input, string? cwd = null, string? transcript = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = SessionId,
            ["transcript_path"] = transcript ?? Path.Combine(defaultConfig, "projects", "-encoded-work", SessionId + ".jsonl"),
            ["cwd"] = cwd ?? work,
            ["permission_mode"] = "dontAsk",
            ["hook_event_name"] = "PreToolUse",
            ["tool_name"] = tool,
            ["tool_input"] = input,
            ["tool_use_id"] = "toolu_01",
        });

    private sealed class FailingReader : TextReader
    {
        public override string ReadToEnd() => throw new IOException("The input closed.");
    }
}
