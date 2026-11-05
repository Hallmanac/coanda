using System.Text.Json;

namespace Coanda.Daemon.Api;

/// <summary>
/// The running daemon's address, written beside the database so the CLI finds it: a loopback
/// port and a token that every call carries. Deleted on a clean stop.
/// </summary>
public sealed record DaemonInfo(int Pid, int Port, string Token, DateTimeOffset StartedAt, string Version)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string BaseAddress => $"http://127.0.0.1:{Port}";

    /// <summary>Writes the address file, readable and writable by its owner alone from the moment it is created.</summary>
    public void Save(NodeHome home)
    {
        using FileStream file = OwnerOnly.Open(home.DaemonInfoPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, this, Json);
    }

    /// <summary>
    /// Reads the address file, or null while there is none to read. The file is opened sharing write
    /// and delete with whoever else holds it, since on Windows a read that shares only read is refused
    /// while the daemon is still writing it; a file caught half written is not valid JSON, so a reader
    /// polling for a starting daemon sees no daemon yet and looks again.
    /// </summary>
    public static DaemonInfo? Load(NodeHome home)
    {
        try
        {
            using var file = new FileStream(home.DaemonInfoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<DaemonInfo>(file);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    public static void Delete(NodeHome home)
    {
        if (File.Exists(home.DaemonInfoPath))
        {
            File.Delete(home.DaemonInfoPath);
        }
    }
}
