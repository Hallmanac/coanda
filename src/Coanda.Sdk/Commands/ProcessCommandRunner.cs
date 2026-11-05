using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Coanda.Sdk.Commands;

/// <summary>
/// The runner that starts real processes, which <c>coa daemon run</c> supplies. The command's
/// standard input is closed as soon as it starts. The command is finished when its own process
/// exits: what it wrote before then is read for a short grace, and a process it started that still
/// holds its output open does not hold the runner, nor keeps it reading once it returns. Cancelling ends the command and every process it
/// started that is still its descendant, and so does the command running past its
/// <see cref="CommandLine.Limit"/>, which answers <see cref="CommandResult.TimedOut"/> with what it
/// wrote until then. Of each of its output, its error output, and both together, only the last
/// <see cref="KeptCharacters"/> characters are kept. Nothing the command writes is logged.
/// </summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    /// <summary>How long output already written is read for after the command's process exits.</summary>
    public static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(1);

    /// <summary>How long a cancelled command's processes are given to go once they are killed.</summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many characters of each of the command's output, its error output, and both together are
    /// kept: the end of each, so a command that writes without end cannot grow the daemon's memory.
    /// </summary>
    public const int KeptCharacters = 1024 * 1024;

    public async Task<CommandResult> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo start = command.VerbatimArguments is { } verbatim
            ? new ProcessStartInfo(command.Program, verbatim)
            : new ProcessStartInfo(command.Program, command.Arguments);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        start.WorkingDirectory = command.WorkingDirectory;
        foreach ((string name, string value) in command.Environment)
        {
            start.Environment[name] = value;
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception unstarted) when (unstarted is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new CommandNotStartedException(command, unstarted.Message, unstarted);
        }

        process.StandardInput.Close();
        var output = new Output();
        using var stopReading = new CancellationTokenSource();
        Task reading = Task.WhenAll(
            output.ReadAsync(process.StandardOutput, error: false, stopReading.Token),
            output.ReadAsync(process.StandardError, error: true, stopReading.Token));

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (command.Limit is { } allowed)
        {
            limit.CancelAfter(allowed);
        }

        try
        {
            bool timedOut = false;
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                End(process);
                timedOut = true;
            }
            catch (OperationCanceledException)
            {
                End(process);
                throw;
            }

            // A process the command started may still hold the output open, so the end of it is not
            // waited for past the grace.
            await Task.WhenAny(reading, Task.Delay(DrainGrace, CancellationToken.None));
            return output.ResultOf(timedOut ? null : process.ExitCode);
        }
        finally
        {
            // Disposing the process leaves streams it handed out open, so the reads a lingering
            // process would otherwise keep alive, and the pipes they hold, are ended here.
            StopReading(process, output, stopReading);
        }
    }

    private static void StopReading(Process process, Output output, CancellationTokenSource stopReading)
    {
        output.Close();
        stopReading.Cancel();
        foreach (Stream stream in (Stream[])[process.StandardOutput.BaseStream, process.StandardError.BaseStream])
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception closing) when (closing is IOException or ObjectDisposedException)
            {
                // The pipe is already closed or broken; there is nothing left to release.
            }
        }
    }

    private static void End(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(KillGrace);
        }
        catch (Exception gone) when (gone is InvalidOperationException or Win32Exception or AggregateException)
        {
            // It exited on its own between the cancellation and the kill.
        }
    }

    /// <summary>
    /// The command's output as it is read: each stream, and both together, each cut to its last
    /// <see cref="KeptCharacters"/> characters. Once closed, nothing more is kept, so a read that
    /// outlives the runner cannot grow it.
    /// </summary>
    private sealed class Output
    {
        private readonly Lock gate = new();
        private bool closed;
        private readonly Tail combined = new();
        private readonly Tail standardOutput = new();
        private readonly Tail standardError = new();

        /// <summary>The result for a command that exited with <paramref name="exitCode"/>, or ran past its limit when that is null.</summary>
        public CommandResult ResultOf(int? exitCode)
        {
            lock (gate)
            {
                CommandResult result = exitCode is { } code
                    ? new CommandResult(code, standardOutput.ToString(), standardError.ToString(), combined.ToString())
                    : CommandResult.LimitReached(standardOutput.ToString(), standardError.ToString(), combined.ToString());
                return result with { Truncated = combined.Cut || standardOutput.Cut || standardError.Cut };
            }
        }

        public void Close()
        {
            lock (gate)
            {
                closed = true;
            }
        }

        public async Task ReadAsync(StreamReader reader, bool error, CancellationToken stop)
        {
            char[] buffer = new char[4096];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), stop)) > 0)
                {
                    lock (gate)
                    {
                        if (closed)
                        {
                            return;
                        }

                        combined.Append(buffer, 0, read);
                        (error ? standardError : standardOutput).Append(buffer, 0, read);
                    }
                }
            }
            catch (Exception ended) when (ended is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The runner returned and ended the read while something the command started held the stream.
            }
        }
    }

    /// <summary>
    /// The last <see cref="KeptCharacters"/> characters of a text as it is appended to. The text may
    /// grow to twice that before its start is dropped, so dropping costs nothing per append on average.
    /// </summary>
    private sealed class Tail
    {
        private readonly StringBuilder text = new();

        private bool dropped;

        /// <summary>Whether any of the start of the text has been dropped.</summary>
        public bool Cut => dropped || text.Length > KeptCharacters;

        public void Append(char[] buffer, int start, int count)
        {
            text.Append(buffer, start, count);
            if (text.Length > 2 * KeptCharacters)
            {
                text.Remove(0, text.Length - KeptCharacters);
                dropped = true;
            }
        }

        public override string ToString() =>
            text.Length > KeptCharacters
                ? text.ToString(text.Length - KeptCharacters, KeptCharacters)
                : text.ToString();
    }
}
