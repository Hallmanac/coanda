using System.Buffers;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// Posts a message into the inbox of a live Claude Code session: connects to the socket its
/// <see cref="SessionInbox"/> names, writes the framed message (<see cref="Frame"/>), and closes the
/// connection. A send succeeds once the whole frame is written, waits for no report of delivery,
/// and is never retried; nothing listening, the inbox closing the connection before the whole frame
/// is written, or a frame longer than <see cref="LongestLine"/> is a failure naming the inbox and
/// never its token.
/// </summary>
/// <remarks>
/// Confirmed against Claude Code 2.1.329, a later version than the 2.1.318 the adapter's other
/// behaviours are confirmed against, which could not be run. On macOS and Linux each session that is
/// not in bare mode, print mode included, binds its inbox as a Unix domain socket and exports its
/// path and token to its hooks and Bash commands as <c>CLAUDE_CODE_MESSAGING_SOCKET</c> and
/// <c>CLAUDE_CODE_MESSAGING_TOKEN</c>. The inbox reads newline-delimited JSON; the CLI's own sender
/// writes an auth line, <c>{"type":"auth","token":"..."}</c>, then the message line,
/// <c>{"type":"user","message":{"role":"user","content":"..."}}</c>, in one write, and refuses to
/// send when the two lines with their newlines are longer than 1,048,576 characters, the most the
/// inbox buffers before it drops the connection. The inbox ignores a message whose content is empty.
/// It writes nothing back on the sender's connection: it reports a message held or refused only by
/// connecting to the sender's own inbox address, which this sender does not name, so a sender that
/// is not a Claude Code session learns nothing beyond a failed connect or a closed connection. On
/// native Windows the inbox is a named pipe, which this sender does not speak, so a send there is
/// refused.
/// </remarks>
public static class ClaudeInbox
{
    /// <summary>The most characters the inbox buffers before it drops the connection, which the framed message may not exceed.</summary>
    public const int LongestLine = 1_048_576;

    /// <summary>
    /// How long a send may take to connect and write the whole frame. The inbox closes a connection
    /// that sends no complete line within 30 seconds, so a send that takes longer cannot land.
    /// </summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonWriterOptions Lines = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The text written to the inbox: the auth line carrying the inbox's token, then the message line, each ending in a newline.</summary>
    public static string Frame(SessionInbox inbox, string message)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(message);
        string auth = Line(writer =>
        {
            writer.WriteString("type", "auth");
            writer.WriteString("token", inbox.Token);
        });
        string user = Line(writer =>
        {
            writer.WriteString("type", "user");
            writer.WriteStartObject("message");
            writer.WriteString("role", "user");
            writer.WriteString("content", message);
            writer.WriteEndObject();
        });
        return auth + user;
    }

    /// <summary>Posts <paramref name="message"/> into the inbox, returning once the whole frame is written and the connection closed.</summary>
    public static async Task SendAsync(SessionInbox inbox, string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(message);
        if (OperatingSystem.IsWindows())
        {
            throw new NotSupportedException(
                $"The claude-code executor does not support the inbox at {inbox.Address} on Windows, where Claude Code's inbox is a named pipe, so nothing was sent.");
        }

        if (message.Length == 0)
        {
            throw new ArgumentException($"The message for the inbox at {inbox.Address} is empty, which the inbox ignores, so nothing was sent.", nameof(message));
        }

        string frame = Frame(inbox, message);
        if (frame.Length > LongestLine)
        {
            throw new InvalidOperationException(
                $"The message framed for the inbox at {inbox.Address} is {Count(frame.Length)} characters long, more than the {Count(LongestLine)} the inbox reads, so nothing was sent.");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(frame);
        UnixDomainSocketEndPoint endpoint;
        try
        {
            endpoint = new UnixDomainSocketEndPoint(inbox.Address);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"The inbox address {inbox.Address} is too long for a socket path, so nothing was sent.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(endpoint, timeout.Token);
        }
        catch (SocketException refused)
        {
            throw new InvalidOperationException(
                $"Nothing listens at the inbox at {inbox.Address} ({refused.SocketErrorCode}), so nothing was sent.", refused);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The inbox at {inbox.Address} did not accept a connection within {Count((int)SendTimeout.TotalSeconds)} seconds, so nothing was sent.");
        }

        int written = 0;
        try
        {
            while (written < bytes.Length)
            {
                written += await socket.SendAsync(bytes.AsMemory(written), SocketFlags.None, timeout.Token);
            }
        }
        catch (SocketException closed)
        {
            throw new InvalidOperationException(
                $"The inbox at {inbox.Address} closed the connection after {Count(written)} of the message's {Count(bytes.Length)} bytes were written ({closed.SocketErrorCode}), so the message was not delivered whole.", closed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"The inbox at {inbox.Address} took {Count(written)} of the message's {Count(bytes.Length)} bytes in {Count((int)SendTimeout.TotalSeconds)} seconds, so the message was not delivered whole.");
        }
    }

    private static string Line(Action<Utf8JsonWriter> members)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Lines))
        {
            writer.WriteStartObject();
            members(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
