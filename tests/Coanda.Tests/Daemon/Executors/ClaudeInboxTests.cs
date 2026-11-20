using System.Text;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Executors.ClaudeCode;

namespace Coanda.Tests.Daemon.Executors;

/// <summary>
/// The Claude Code executor's send to an inbox handle, against a fake inbox listening on a local Unix
/// socket, and the refusals that keep the executor from doing anything else with such a handle.
/// </summary>
public sealed class ClaudeInboxTests : IDisposable
{
    private const string Token = "inbox-token-0123456789abcdef";
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TemporaryHome temp = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task A_send_to_an_inbox_handle_writes_the_auth_line_then_the_message_line_and_closes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var inbox = new FakeInbox();
        SessionHandle handle = SessionHandle.ForInbox("window", new SessionInbox(inbox.Address, Token));

        await Executor().SendAsync(handle, "Line one\nsays \"hi\" in caf\u00e9 <now>", None);

        byte[] received = Assert.Single(await inbox.ReceivedAsync(1, None));
        Assert.Equal(
            Encoding.UTF8.GetBytes(
                "{\"type\":\"auth\",\"token\":\"inbox-token-0123456789abcdef\"}\n"
                + "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"Line one\\nsays \\\"hi\\\" in caf\u00e9 <now>\"}}\n"),
            received);
    }

    [Fact]
    public async Task A_new_executor_instance_delivers_to_the_same_handle_as_after_a_daemon_restart()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var inbox = new FakeInbox();
        var address = new SessionInbox(inbox.Address, Token);
        SessionHandle handle = SessionHandle.ForInbox("window", address);

        await Executor().SendAsync(handle, "first", None);
        await Executor().SendAsync(handle, "second", None);

        IReadOnlyList<byte[]> received = await inbox.ReceivedAsync(2, None);
        Assert.Equal(
            [ClaudeInbox.Frame(address, "first"), ClaudeInbox.Frame(address, "second")],
            received.Select(bytes => Encoding.UTF8.GetString(bytes)));
        Assert.Empty(await Executor().ListAsync(None));
    }

    [Fact]
    public async Task A_send_fails_naming_the_inbox_and_not_its_token_when_nothing_listens_at_its_address()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string address = Path.Combine(Path.GetTempPath(), $"gone-{Guid.NewGuid().ToString("N")[..8]}.sock");
        SessionHandle handle = SessionHandle.ForInbox("window", new SessionInbox(address, Token));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Executor().SendAsync(handle, "hello", None).AsTask());

        Assert.StartsWith($"Nothing listens at the inbox at {address} (", failure.Message, StringComparison.Ordinal);
        Assert.EndsWith("), so nothing was sent.", failure.Message, StringComparison.Ordinal);
        Withheld(failure);

        string tooLong = Path.Combine(temp.Home.Path, new string('s', 120) + ".sock");
        InvalidOperationException unreachable = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Executor().SendAsync(SessionHandle.ForInbox("window", new SessionInbox(tooLong, Token)), "hello", None).AsTask());
        Assert.Equal($"The inbox address {tooLong} is too long for a socket path, so nothing was sent.", unreachable.Message);
        Withheld(unreachable);
    }

    [Fact]
    public async Task A_send_fails_naming_the_inbox_when_it_closes_the_connection_before_the_whole_message_is_written()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Longer than any socket buffer, so the inbox closes the connection while the rest is still being written.
        using var inbox = new FakeInbox(closeAfter: 16);
        SessionHandle handle = SessionHandle.ForInbox("window", new SessionInbox(inbox.Address, Token));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Executor().SendAsync(handle, new string('x', 1_000_000), None).AsTask());

        Assert.StartsWith($"The inbox at {inbox.Address} closed the connection after ", failure.Message, StringComparison.Ordinal);
        Assert.Contains("so the message was not delivered whole.", failure.Message, StringComparison.Ordinal);
        Withheld(failure);
    }

    [Fact]
    public async Task A_message_whose_frame_is_the_longest_line_the_inbox_reads_is_sent_and_one_character_longer_is_refused_unsent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var inbox = new FakeInbox();
        var address = new SessionInbox(inbox.Address, Token);
        SessionHandle handle = SessionHandle.ForInbox("window", address);
        int longest = ClaudeInbox.LongestLine - ClaudeInbox.Frame(address, string.Empty).Length;

        await Executor().SendAsync(handle, new string('y', longest), None);
        Assert.Equal(ClaudeInbox.LongestLine, Assert.Single(await inbox.ReceivedAsync(1, None)).Length);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Executor().SendAsync(handle, new string('y', longest + 1), None).AsTask());
        Assert.Equal(
            $"The message framed for the inbox at {inbox.Address} is 1,048,577 characters long, more than the 1,048,576 the inbox reads, so nothing was sent.",
            failure.Message);
        Withheld(failure);
        await Task.Delay(100);
        Assert.Single(inbox.Received);
    }

    [Fact]
    public async Task An_empty_message_is_refused_unsent_because_the_inbox_ignores_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var inbox = new FakeInbox();
        SessionHandle handle = SessionHandle.ForInbox("window", new SessionInbox(inbox.Address, Token));

        ArgumentException failure = await Assert.ThrowsAsync<ArgumentException>(() => Executor().SendAsync(handle, string.Empty, None).AsTask());

        Assert.StartsWith($"The message for the inbox at {inbox.Address} is empty", failure.Message, StringComparison.Ordinal);
        Withheld(failure);
    }

    [Fact]
    public async Task On_windows_a_send_to_an_inbox_handle_is_refused_because_the_inbox_there_is_a_named_pipe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string address = @"\\.\pipe\claude-inbox";
        SessionHandle handle = SessionHandle.ForInbox("window", new SessionInbox(address, Token));

        NotSupportedException failure = await Assert.ThrowsAsync<NotSupportedException>(() => Executor().SendAsync(handle, "hello", None).AsTask());

        Assert.Equal(
            $"The claude-code executor does not support the inbox at {address} on Windows, where Claude Code's inbox is a named pipe, so nothing was sent.",
            failure.Message);
        Withheld(failure);
    }

    [Fact]
    public async Task Collect_resume_stop_and_reattach_each_refuse_an_inbox_handle_and_list_never_returns_one()
    {
        string address = Path.Combine(temp.Home.Path, "window.sock");
        SessionHandle handle = SessionHandle.ForInbox("window", new SessionInbox(address, Token));
        ClaudeCodeExecutor executor = Executor();

        IReadOnlyList<(string Action, Func<Task> Call)> calls =
        [
            ("collect from", () => executor.CollectAsync(handle, None).AsTask()),
            ("resume", () => executor.ResumeAsync(handle, "continue", None).AsTask()),
            ("stop", () => executor.StopAsync(handle, None).AsTask()),
            ("reattach", () => executor.ReattachAsync(handle, access: null, None).AsTask()),
        ];
        foreach ((string action, Func<Task> call) in calls)
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(call);
            Assert.Equal(
                $"The claude-code executor cannot {action} session {handle.Id}: its handle names the inbox at {address} of a session the executor did not start, and the executor only sends to such a handle.",
                failure.Message);
            Withheld(failure);
        }

        Assert.Empty(await executor.ListAsync(None));
        Assert.False(Directory.Exists(temp.Home.SessionsPath) && Directory.EnumerateFileSystemEntries(temp.Home.SessionsPath).Any(), "nothing was recorded for the handle");
    }

    [Fact]
    public void The_token_is_absent_from_the_printed_form_of_the_inbox_and_of_its_handle()
    {
        string address = Path.Combine(Path.GetTempPath(), "cc-socks", "42.sock");
        var inbox = new SessionInbox(address, Token);
        SessionHandle handle = SessionHandle.ForInbox("window", inbox);

        Assert.Equal($"SessionInbox {{ Address = {address}, Token = (withheld) }}", inbox.ToString());
        Assert.Equal($"SessionHandle {{ Id = {handle.Id}, Agent = window, Inbox = {inbox} }}", handle.ToString());
        Assert.DoesNotContain(Token, handle.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_inbox_needs_an_absolute_address_and_a_token_and_a_refusal_never_names_the_token()
    {
        ArgumentException relative = Assert.Throws<ArgumentException>(() => new SessionInbox("cc-socks/42.sock", Token));
        Withheld(relative);
        Assert.Throws<ArgumentException>(() => new SessionInbox(Path.Combine(Path.GetTempPath(), "cc-socks", "42.sock"), " "));
        Assert.Throws<ArgumentException>(() => new SessionInbox(" ", Token));
    }

    [Fact]
    public async Task The_scripted_executor_records_each_send_to_an_inbox_handle_and_refuses_it_everything_else()
    {
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>());
        SessionHandle handle = SessionHandle.ForInbox("window", new SessionInbox(Path.Combine(Path.GetTempPath(), "cc-socks", "42.sock"), Token));

        await executor.SendAsync(handle, "first", None);
        await executor.SendAsync(handle, "second", None);

        Assert.Equal([new ScriptedMessage(handle, "first"), new ScriptedMessage(handle, "second")], executor.Sends);
        IReadOnlyList<Func<Task>> calls =
        [
            () => executor.CollectAsync(handle, None).AsTask(),
            () => executor.ResumeAsync(handle, "continue", None).AsTask(),
            () => executor.StopAsync(handle, None).AsTask(),
            () => executor.ReattachAsync(handle, access: null, None).AsTask(),
        ];
        foreach (Func<Task> call in calls)
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(call);
            Assert.StartsWith("The scripted executor cannot ", failure.Message, StringComparison.Ordinal);
            Withheld(failure);
        }

        Assert.Empty(await executor.ListAsync(None));
        Assert.Empty(executor.Resumes);
        Assert.Empty(executor.Stops);
    }

    private static void Withheld(Exception failure) =>
        Assert.DoesNotContain(Token, failure.ToString(), StringComparison.Ordinal);

    private ClaudeCodeExecutor Executor() => new("claude", temp.Home, new Dictionary<string, string>());
}
