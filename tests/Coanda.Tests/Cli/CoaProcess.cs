using System.Diagnostics;
using Coanda.Daemon;
using Coanda.Daemon.Executors;
using Xunit.Sdk;

namespace Coanda.Tests.Cli;

/// <summary>What one run of the built <c>coa</c> exited with and printed.</summary>
public sealed record Ran(int ExitCode, string Output, string Error);

/// <summary>Runs the built <c>coa</c> as a process against a node home whose daemon the test started.</summary>
public static class CoaProcess
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);
    private static readonly string Coa = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "coa.exe" : "coa");

    /// <summary>Runs <c>coa</c> with <paramref name="arguments"/> against <paramref name="home"/> until it exits.</summary>
    public static async Task<Ran> RunAsync(NodeHome home, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo(Coa) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[NodeHome.EnvironmentVariable] = home.Path;
        start.Environment.Remove(SessionAccess.TokenVariable);
        start.Environment.Remove(SessionAccess.HomeVariable);
        start.Environment.Remove("COANDA_PROJECT");

        using Process process = Process.Start(start) ?? throw new XunitException($"{Coa} did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(Bound, cancellationToken);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw new XunitException($"coa {string.Join(' ', arguments)} did not return within {Bound.TotalSeconds:0} seconds.");
        }

        return new Ran(process.ExitCode, await output.WaitAsync(Bound, cancellationToken), await error.WaitAsync(Bound, cancellationToken));
    }
}
