using Coanda.Sdk.Repositories;
using Coanda.Tests.Daemon;

namespace Coanda.Tests.Sdk.Repositories;

/// <summary>
/// The rule that finds Git for Windows' real git behind its launcher, and the process-wide lookup
/// that holds what it found, run against fake install layouts made with the host's own path
/// conventions, so they run on any operating system.
/// </summary>
public sealed class GitExecutableTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "coanda-git-executable-tests", Guid.NewGuid().ToString("N"));

    public GitExecutableTests() => Directory.CreateDirectory(scratch);

    public void Dispose() => TemporaryHome.Remove(scratch);

    [Theory]
    [InlineData("cmd")]
    [InlineData("bin")]
    [InlineData("CMD")]
    [InlineData("Bin")]
    public void A_launcher_in_an_installs_cmd_or_bin_directory_answers_the_real_git(string launcherDirectory)
    {
        string install = Install("git", launcherDirectory, "mingw64");

        Assert.Equal(Real(install, "mingw64"), GitExecutable.Resolve(PathOf(Path.Combine(install, launcherDirectory))));
    }

    [Fact]
    public void A_git_already_inside_a_flavours_bin_directory_answers_nothing()
    {
        string install = Install("git", "cmd", "ucrt64", "mingw64");

        Assert.Null(GitExecutable.Resolve(PathOf(Path.Combine(install, "mingw64", "bin"))));
    }

    [Fact]
    public void A_git_outside_any_install_layout_answers_nothing()
    {
        string install = Install("git", "cmd", "ucrt64");
        string tools = Path.Combine(install, "tools");
        Touch(Path.Combine(tools, "git.exe"));

        Assert.Null(GitExecutable.Resolve(PathOf(tools)));
    }

    [Fact]
    public void A_launcher_layout_with_none_of_the_three_real_executables_answers_nothing()
    {
        string install = Install("git", "cmd");
        Touch(Path.Combine(install, "usr", "bin", "git.exe"));

        Assert.Null(GitExecutable.Resolve(PathOf(Path.Combine(install, "cmd"))));
    }

    [Theory]
    [InlineData(new[] { "ucrt64", "mingw64", "clangarm64" }, "ucrt64")]
    [InlineData(new[] { "mingw64", "clangarm64" }, "mingw64")]
    [InlineData(new[] { "ucrt64", "clangarm64" }, "ucrt64")]
    [InlineData(new[] { "clangarm64" }, "clangarm64")]
    public void The_first_flavour_that_exists_is_answered_in_order(string[] flavours, string expected)
    {
        string install = Install("git", "cmd", flavours);

        Assert.Equal(Real(install, expected), GitExecutable.Resolve(PathOf(Path.Combine(install, "cmd"))));
    }

    [Fact]
    public void No_git_on_the_path_answers_nothing()
    {
        string empty = Path.Combine(scratch, "empty");
        Directory.CreateDirectory(empty);
        string install = Install("git", "cmd", "ucrt64");
        File.Delete(Path.Combine(install, "cmd", "git.exe"));

        Assert.Null(GitExecutable.Resolve(PathOf(empty, Path.Combine(install, "cmd"), Path.Combine(scratch, "missing"))));
        Assert.Null(GitExecutable.Resolve(string.Empty));
        Assert.Null(GitExecutable.Resolve(null));
    }

    [Fact]
    public void Only_the_first_git_on_the_path_is_considered()
    {
        string install = Install("git", "cmd", "ucrt64");
        string tools = Path.Combine(scratch, "tools");
        Touch(Path.Combine(tools, "git.exe"));
        string empty = Path.Combine(scratch, "empty");
        Directory.CreateDirectory(empty);

        Assert.Null(GitExecutable.Resolve(PathOf(tools, Path.Combine(install, "cmd"))));
        Assert.Equal(Real(install, "ucrt64"), GitExecutable.Resolve(PathOf(empty, Path.Combine(install, "cmd"), tools)));
    }

    [Fact]
    public void The_launcher_is_looked_up_once_and_the_real_git_it_resolved_is_reused_while_it_exists()
    {
        string install = Install("git", "cmd", "mingw64");
        int lookups = 0;
        var git = new GitExecutable(() =>
        {
            lookups++;
            return PathOf(Path.Combine(install, "cmd"));
        });

        Assert.Equal(Real(install, "mingw64"), git.Current());

        // A flavour earlier in the order appears; the one already resolved still exists, so it is kept.
        Touch(Real(install, "ucrt64"));
        Assert.Equal(Real(install, "mingw64"), git.Current());
        Assert.Equal(Real(install, "mingw64"), git.Current());
        Assert.Equal(1, lookups);
    }

    [Fact]
    public void Once_the_resolved_git_is_gone_the_next_lookup_answers_another_flavour_then_nothing_then_the_one_put_back()
    {
        string install = Install("git", "bin", "mingw64", "clangarm64");
        int lookups = 0;
        var git = new GitExecutable(() =>
        {
            lookups++;
            return PathOf(Path.Combine(install, "bin"));
        });
        Assert.Equal(Real(install, "mingw64"), git.Current());

        // The launcher itself is gone too: the install it was found in is what is looked in again.
        File.Delete(Path.Combine(install, "bin", "git.exe"));
        File.Delete(Real(install, "mingw64"));
        Assert.Equal(Real(install, "clangarm64"), git.Current());

        File.Delete(Real(install, "clangarm64"));
        Assert.Null(git.Current());
        Assert.Null(git.Current());

        Touch(Real(install, "mingw64"));
        Assert.Equal(Real(install, "mingw64"), git.Current());
        Assert.Equal(1, lookups);
    }

    [Fact]
    public void A_real_git_that_appears_in_a_launchers_install_after_the_lookup_is_answered()
    {
        string install = Install("git", "cmd");
        var git = new GitExecutable(() => PathOf(Path.Combine(install, "cmd")));
        Assert.Null(git.Current());

        Touch(Real(install, "ucrt64"));

        Assert.Equal(Real(install, "ucrt64"), git.Current());
    }

    [Fact]
    public void A_path_with_no_launcher_answers_nothing_on_every_call()
    {
        string tools = Path.Combine(scratch, "tools");
        Touch(Path.Combine(tools, "git.exe"));
        int lookups = 0;
        var git = new GitExecutable(() =>
        {
            lookups++;
            return PathOf(tools);
        });

        Assert.Null(git.Current());
        Touch(Path.Combine(scratch, "ucrt64", "bin", "git.exe"));
        Assert.Null(git.Current());
        Assert.Equal(1, lookups);
    }

    [Fact]
    public void The_process_wide_lookup_answers_nothing_off_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            // On Windows it answers from the runner's own PATH, which this test does not control.
            return;
        }

        Assert.Null(GitExecutable.ForProcess.Current());
    }

    /// <summary>A fake install named <paramref name="name"/>: the launcher in <paramref name="launcherDirectory"/> and a real git in each flavour named.</summary>
    private string Install(string name, string launcherDirectory, params string[] flavours)
    {
        string install = Path.Combine(scratch, name);
        Touch(Path.Combine(install, launcherDirectory, "git.exe"));
        foreach (string flavour in flavours)
        {
            Touch(Real(install, flavour));
        }

        return install;
    }

    private static string Real(string install, string flavour) => Path.Combine(install, flavour, "bin", "git.exe");

    private static string PathOf(params string[] directories) => string.Join(Path.PathSeparator, directories);

    private static void Touch(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file) ?? file);
        File.WriteAllBytes(file, []);
    }
}
