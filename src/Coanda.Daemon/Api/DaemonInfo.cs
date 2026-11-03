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

    public void Save(NodeHome home) => File.WriteAllText(home.DaemonInfoPath, JsonSerializer.Serialize(this, Json));

    public static DaemonInfo? Load(NodeHome home)
    {
        if (!File.Exists(home.DaemonInfoPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DaemonInfo>(File.ReadAllText(home.DaemonInfoPath));
        }
        catch (JsonException)
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
