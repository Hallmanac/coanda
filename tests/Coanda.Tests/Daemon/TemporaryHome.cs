using Coanda.Daemon;

namespace Coanda.Tests.Daemon;

/// <summary>A throwaway node home, so a test's SQLite file never touches ~/.coanda.</summary>
public sealed class TemporaryHome : IDisposable
{
    public NodeHome Home { get; } = new NodeHome(Path.Combine(Path.GetTempPath(), "coanda-tests", Guid.NewGuid().ToString("N"))).EnsureExists();

    public void Dispose() => Remove(Home.Path);

    /// <summary>
    /// Deletes a test's directory, first clearing the read-only mark git leaves on its objects, because Windows
    /// refuses to delete a read-only file. A delete that still fails leaves the directory for the temp folder's
    /// own cleanup rather than failing the test that used it.
    /// </summary>
    public static void Remove(string path)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }
    }
}
