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

        File.Exists(Path.Combine(outside, "task.md")).Should().BeFalse("the linked directory is never written into");
        new DirectoryInfo(Path.Combine(work, ".coanda")).LinkTarget.Should().BeNull();
        File.ReadAllText(Path.Combine(work, ".coanda", "task.md")).Should().Be("fresh record");

        File.CreateSymbolicLink(Path.Combine(work, ".coanda", "prompt.md"), victim);
        SessionFiles.Write(work, SessionFiles.PromptFile, "fresh context");

        File.ReadAllText(victim).Should().Be("untouched");
        new FileInfo(Path.Combine(work, ".coanda", "prompt.md")).LinkTarget.Should().BeNull();
        File.ReadAllText(Path.Combine(work, ".coanda", "prompt.md")).Should().Be("fresh context");
    }
}
