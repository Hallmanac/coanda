using Coanda.Daemon.Executors;

namespace Coanda.Tests.Daemon.Executors;

public sealed class SessionFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coanda-session-files", Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void A_link_a_session_left_in_place_of_a_file_or_of_coanda_itself_is_replaced_never_written_through()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a link there needs a privilege the test runner may not hold.
        }

        string work = Path.Combine(root, "work");
        string outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(outside);
        string victim = Path.Combine(outside, "victim.md");
        File.WriteAllText(victim, "untouched");
        Directory.CreateSymbolicLink(Path.Combine(work, ".coanda"), outside);

        SessionFiles.Write(work, SessionFiles.TaskFile, "fresh record");

        Assert.False(File.Exists(Path.Combine(outside, "task.md")), "the linked directory is never written into");
        Assert.Null(new DirectoryInfo(Path.Combine(work, ".coanda")).LinkTarget);
        Assert.Equal("fresh record", File.ReadAllText(Path.Combine(work, ".coanda", "task.md")));

        File.CreateSymbolicLink(Path.Combine(work, ".coanda", "prompt.md"), victim);
        SessionFiles.Write(work, SessionFiles.PromptFile, "fresh context");

        Assert.Equal("untouched", File.ReadAllText(victim));
        Assert.Null(new FileInfo(Path.Combine(work, ".coanda", "prompt.md")).LinkTarget);
        Assert.Equal("fresh context", File.ReadAllText(Path.Combine(work, ".coanda", "prompt.md")));
    }
}
