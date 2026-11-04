using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Coanda.Daemon.Commands;

/// <summary>
/// The runner that starts real processes, which <c>coa daemon run</c> supplies. The command's
/// standard input is closed as soon as it starts. The command is finished when its own process
/// exits: what it wrote before then is read for a short grace, and a process it started that still
/// holds its output open does not hold the runner. Cancelling ends the command and every process it
/// started that is still its descendant. Nothing the command writes is logged.
/// </summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    /// <summary>How long output already written is read for after the command's process exits.</summary>
    public static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(1);

    /// <summary>How long a cancelled command's processes are given to go once they are killed.</summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(10);

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
        Task reading = Task.WhenAll(
            output.ReadAsync(process.StandardOutput, error: false),
            output.ReadAsync(process.StandardError, error: true));

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            End(process);
            throw;
        }

        // A process the command started may still hold the output open, so the end of it is not
        // waited for past the grace.
        await Task.WhenAny(reading, Task.Delay(DrainGrace, CancellationToken.None));
        return new CommandResult(process.ExitCode, output.StandardOutput, output.StandardError, output.Combined);
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

    /// <summary>The command's output as it is read: each stream, and both together.</summary>
    private sealed class Output
    {
        private readonly Lock gate = new();
        private readonly StringBuilder combined = new();
        private readonly StringBuilder standardOutput = new();
        private readonly StringBuilder standardError = new();

        public string Combined => Read(combined);

        public string StandardOutput => Read(standardOutput);

        public string StandardError => Read(standardError);

        public async Task ReadAsync(StreamReader reader, bool error)
        {
            char[] buffer = new char[4096];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
                {
                    lock (gate)
                    {
                        combined.Append(buffer, 0, read);
                        (error ? standardError : standardOutput).Append(buffer, 0, read);
                    }
                }
            }
            catch (Exception closed) when (closed is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The runner returned and disposed the process while something it started held the stream.
            }
        }

        private string Read(StringBuilder text)
        {
            lock (gate)
            {
                return text.ToString();
            }
        }
    }
}
