using Coanda.Daemon;

namespace Coanda.Tests.Daemon;

/// <summary>A throwaway node home, so a test's SQLite file never touches ~/.coanda.</summary>
public sealed class TemporaryHome : IDisposable
{
    public NodeHome Home { get; } = new NodeHome(Path.Combine(Path.GetTempPath(), "coanda-tests", Guid.NewGuid().ToString("N"))).EnsureExists();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Home.Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
