using System.ComponentModel;
using Coanda.Core;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Store;
using Spectre.Console.Cli;

namespace Coanda.Cli.Commands;

/// <summary>
/// Writes a full export of the node's record, every event in this build's current shape, to an
/// owner-only file under the node home. It reads the home directly rather than through the daemon, so
/// it refuses while a daemon answers on the home, and the home's lock refuses it while one is starting.
/// </summary>
public sealed class RecordExportCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        NodeHome home = NodeHome.Resolve();
        await RecordCommands.RefuseWhileRunningAsync(home, "exported", cancellationToken);
        RecordExportResult result = await RecordCommands.QuietlyAsync(() => RecordExport.WriteAsync(home, TimeProvider.System, cancellationToken));
        string last = result.LastEvent is { } named
            ? $"{named.Id} at sequence {named.Sequence}"
            : "none";
        Console.WriteLine($"Exported {result.Events} events; the record's last event is {last}. The export is at:");
        Console.WriteLine(result.Path);
        Console.WriteLine("Rebuild the record from it with:");
        Console.WriteLine($"coa record rebuild \"{result.Path}\"");
        return 0;
    }
}

public sealed class RecordRebuildSettings : CommandSettings
{
    [CommandArgument(0, "<EXPORT>")]
    [Description("The export coa record export wrote, which names the record's last event; the rebuild refuses if the record has moved since.")]
    public required string Export { get; init; }
}

/// <summary>
/// Rebuilds the node's record from an export of it, moving the existing database and its companion
/// files aside, unchanged, and saying where. It refuses while a daemon answers on the home, and the
/// home's lock refuses it while one is starting.
/// </summary>
public sealed class RecordRebuildCommand : AsyncCommand<RecordRebuildSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, RecordRebuildSettings settings, CancellationToken cancellationToken)
    {
        NodeHome home = NodeHome.Resolve();
        await RecordCommands.RefuseWhileRunningAsync(home, "rebuilt", cancellationToken);
        RecordRebuildResult result = await RecordCommands.QuietlyAsync(() => RecordRebuild.RunAsync(home, Path.GetFullPath(settings.Export), TimeProvider.System, cancellationToken));
        Console.WriteLine($"Rebuilt the record at {home.DatabasePath} with {result.Events} events.");
        Console.WriteLine("The record it replaced, with its companion files, is unchanged in:");
        Console.WriteLine(result.MovedAside);
        return 0;
    }
}

internal static class RecordCommands
{
    /// <summary>
    /// Runs <paramref name="run"/> with standard output discarded: the store's schema migration prints each
    /// statement it applies there, which says nothing to the owner here. A refusal is an exception, which
    /// the command's handler prints once output is restored.
    /// </summary>
    public static async Task<T> QuietlyAsync<T>(Func<Task<T>> run)
    {
        TextWriter output = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            return await run();
        }
        finally
        {
            Console.SetOut(output);
        }
    }

    /// <summary>Refuses when a daemon answers on the home: the record is the daemon's to write while it runs.</summary>
    public static async Task RefuseWhileRunningAsync(NodeHome home, string what, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(home, sessionToken: null);
        if (await client.FindRunningAsync(cancellationToken) is DaemonInfo running)
        {
            throw new DomainException(
                $"A daemon is running on the node home {home.Path} ({running.BaseAddress}, pid {running.Pid}), so the record was not {what}. Stop it with coa daemon stop, then run this again.");
        }
    }
}
