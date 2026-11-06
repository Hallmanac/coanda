namespace Coanda.Tests.Daemon;

/// <summary>
/// A temporary home is deleted on disposal even when it holds a clone, whose objects git writes read-only, and a
/// delete that still fails does not fail the test that used the home.
/// </summary>
public sealed class TemporaryHomeTests
{
    [Fact]
    public void A_home_holding_read_only_files_is_deleted_on_disposal()
    {
        var temp = new TemporaryHome();
        string objects = Path.Combine(temp.Home.Path, "projects", "p", "repos", "r", "objects", "ab");
        Directory.CreateDirectory(objects);
        string file = Path.Combine(objects, "cdef");
        File.WriteAllText(file, "object");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        temp.Dispose();

        Assert.False(Directory.Exists(temp.Home.Path));
    }

    [Fact]
    public void A_delete_that_still_fails_is_tolerated()
    {
        // A directory its owner may not write keeps its entries on Unix whatever their marks, which is the one
        // refusal a test can make without another process holding a file open; Windows has no such mode.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var temp = new TemporaryHome();
        string locked = Path.Combine(temp.Home.Path, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "kept"), "kept");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            temp.Dispose();

            Assert.True(File.Exists(Path.Combine(locked, "kept")));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            TemporaryHome.Remove(temp.Home.Path);
        }

        Assert.False(Directory.Exists(temp.Home.Path));
    }
}
