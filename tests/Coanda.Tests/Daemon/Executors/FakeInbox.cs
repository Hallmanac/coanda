using System.Net.Sockets;

namespace Coanda.Tests.Daemon.Executors;

/// <summary>
/// A Claude Code session's inbox stood in for by a Unix domain socket listening in a directory of its
/// own: it records the bytes each connection sent until the sender closed it, or, given
/// <c>closeAfter</c>, closes each connection once it has read that many bytes, as an inbox that drops
/// a sender does.
/// </summary>
internal sealed class FakeInbox : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"inbox-{Guid.NewGuid().ToString("N")[..8]}");
    private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource stopping = new();
    private readonly List<byte[]> received = [];
    private readonly Lock gate = new();
    private readonly Task accepting;

    public FakeInbox(int? closeAfter = null)
    {
        Directory.CreateDirectory(directory);
        Address = Path.Combine(directory, "inbox.sock");
        listener.Bind(new UnixDomainSocketEndPoint(Address));
        listener.Listen(4);
        accepting = AcceptAsync(closeAfter, stopping.Token);
    }

    /// <summary>The path the inbox listens at.</summary>
    public string Address { get; }

    /// <summary>What each connection sent, in the order the connections closed.</summary>
    public IReadOnlyList<byte[]> Received
    {
        get
        {
            lock (gate)
            {
                return [.. received];
            }
        }
    }

    /// <summary>Waits until <paramref name="count"/> connections have closed, and returns what each sent.</summary>
    public async Task<IReadOnlyList<byte[]>> ReceivedAsync(int count, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (Received.Count < count)
        {
            await Task.Delay(10, timeout.Token);
        }

        return Received;
    }

    public void Dispose()
    {
        stopping.Cancel();
        listener.Dispose();
        try
        {
            accepting.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        stopping.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private async Task AcceptAsync(int? closeAfter, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(cancellationToken);
            }
            catch (Exception stopped) when (stopped is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            using (connection)
            {
                byte[] bytes = await ReadAsync(connection, closeAfter, cancellationToken);
                lock (gate)
                {
                    received.Add(bytes);
                }
            }
        }
    }

    private static async Task<byte[]> ReadAsync(Socket connection, int? closeAfter, CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (closeAfter is not { } limit || bytes.Length < limit)
            {
                int wanted = closeAfter is { } most ? (int)Math.Min(buffer.Length, most - bytes.Length) : buffer.Length;
                int read = await connection.ReceiveAsync(buffer.AsMemory(0, wanted), SocketFlags.None, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                bytes.Write(buffer, 0, read);
            }
        }
        catch (Exception stopped) when (stopped is OperationCanceledException or SocketException)
        {
        }

        return bytes.ToArray();
    }
}
