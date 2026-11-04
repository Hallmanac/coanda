using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Daemon;
using Coanda.Daemon.Executors.ClaudeCode;

namespace Coanda.Tests.Daemon.Executors;

/// <summary>
/// The fake claude executable built from the solution (<c>tests/Coanda.FakeClaude</c>), driven from a
/// control directory of its own: the script each start plays, and the records each start leaves.
/// The environment it hands the adapter is built here, never taken from the test process's own.
/// </summary>
public sealed class FakeClaude : IDisposable
{
    public FakeClaude(params object[] script)
    {
        Directory.CreateDirectory(Control);
        Script(script);
    }

    /// <summary>The fake's executable, beside the test assembly.</summary>
    public static string Executable { get; } = Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "Coanda.FakeClaude.exe" : "Coanda.FakeClaude");

    public string Control { get; } = Path.Combine(Path.GetTempPath(), "coanda-fake-claude", Guid.NewGuid().ToString("N"));

    /// <summary>The PATH of the test process and the fake's control directory, plus whatever the test adds.</summary>
    public Dictionary<string, string> Environment(params (string Name, string Value)[] more)
    {
        var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            ["PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            [Coanda.Testing.FakeClaude.Program.ControlVariable] = Control,
        };
        if (OperatingSystem.IsWindows())
        {
            // A .NET apphost needs these to find the runtime and a temporary directory.
            foreach (string name in new[] { "SystemRoot", "TEMP", "TMP", "USERPROFILE", "DOTNET_ROOT", "ProgramFiles" })
            {
                if (System.Environment.GetEnvironmentVariable(name) is { } value)
                {
                    environment[name] = value;
                }
            }
        }
        else if (System.Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } root)
        {
            environment["DOTNET_ROOT"] = root;
        }

        foreach ((string name, string value) in more)
        {
            environment[name] = value;
        }

        return environment;
    }

    public ClaudeCodeExecutor Executor(NodeHome home, IReadOnlyDictionary<string, string>? environment = null, string? coaDirectory = null) =>
        new(Executable, home, environment ?? Environment(), coaDirectory);

    /// <summary>Replaces the script; each entry is serialized as the fake reads it.</summary>
    public void Script(params object[] entries) =>
        File.WriteAllText(Path.Combine(Control, "script.json"), JsonSerializer.Serialize(entries));

    /// <summary>Releases every start whose entry waits for <paramref name="name"/>.</summary>
    public void Release(string name = "release") => File.WriteAllText(Path.Combine(Control, name), "released");

    /// <summary>
    /// One record per process start, in start order. A start holds the file to itself while it
    /// appends its record, so a read that meets one waits for it, as the fake's own writers do.
    /// </summary>
    public IReadOnlyList<Start> Starts
    {
        get
        {
            string path = Path.Combine(Control, "records.jsonl");
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return File.Exists(path)
                        ? [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(Start.From)]
                        : [];
                }
                catch (IOException) when (attempt < 50)
                {
                    Thread.Sleep(20);
                }
            }
        }
    }

    /// <summary>The pid of the child process an entry with <c>child</c> started, once it has written it.</summary>
    public int? ChildPid
    {
        get
        {
            string path = Path.Combine(Control, "child.pid");
            try
            {
                return File.Exists(path) && int.TryParse(File.ReadAllText(path), out int pid)
                    ? pid
                    : null;
            }
            catch (IOException)
            {
                // Windows refuses a read while the child still has the file open; it is not there yet.
                return null;
            }
        }
    }

    /// <summary>Whether a process with the id is running, as the system reports it, so one that has exited but not been reaped yet still counts.</summary>
    public static bool Running(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the process has exited, counting one that has exited but not been reaped yet: a session
    /// process a test's first executor started is a child of the test process, and stays in the process
    /// table after it exits until something waits on it, where a daemon that exited would have left
    /// it to the system to reap.
    /// </summary>
    public static bool Finished(int pid)
    {
        if (OperatingSystem.IsWindows() || !Running(pid))
        {
            return !Running(pid);
        }

        if (OperatingSystem.IsLinux())
        {
            string stat = $"/proc/{pid}/stat";
            return !File.Exists(stat) || File.ReadAllText(stat).Split(')')[^1].TrimStart().StartsWith('Z');
        }

        using Process ps = Process.Start(new ProcessStartInfo("ps", ["-o", "stat=", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)]) { RedirectStandardOutput = true })
            ?? throw new InvalidOperationException("ps did not start.");
        string state = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();
        return state.Length == 0 || state.StartsWith('Z');
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Control, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A successful result whose reply is <paramref name="reply"/>, with figures in <c>model_usage_since_last_result</c>.</summary>
    public static object Answering(string? reply, decimal cost, decimal totalCost, long input = 10, long output = 20, long cacheCreation = 30, long cacheRead = 40, int turns = 2, long durationMs = 1500, string? waitFor = null) => new
    {
        reply,
        waitFor,
        totalCost,
        turns,
        durationMs,
        usage = new { input, output, cacheCreation, cacheRead },
        sinceLast = new Dictionary<string, object> { ["claude-test"] = new { input, output, cacheCreation, cacheRead, cost } },
    };

    /// <summary>One start's record: its arguments, environment, working directory, standard input, and the persona file it found, or null when there was none.</summary>
    public sealed record Start(int Pid, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, string WorkingDirectory, string Input, string? Persona)
    {
        /// <summary>The value after <paramref name="flag"/>, or null when the arguments do not hold it.</summary>
        public string? After(string flag)
        {
            int index = Arguments.ToList().IndexOf(flag);
            return index >= 0 && index + 1 < Arguments.Count ? Arguments[index + 1] : null;
        }

        /// <summary>The arguments with the session flag and its value and the spend cap and its value taken out.</summary>
        public IReadOnlyList<string> Shared
        {
            get
            {
                List<string> shared = [.. Arguments];
                foreach (string flag in new[] { "--session-id", "--resume", "--max-budget-usd" })
                {
                    int index = shared.IndexOf(flag);
                    if (index >= 0)
                    {
                        shared.RemoveRange(index, 2);
                    }
                }

                return shared;
            }
        }

        internal static Start From(string line)
        {
            JsonNode record = JsonNode.Parse(line) ?? throw new InvalidOperationException("An empty record.");
            return new Start(
                (int?)record["pid"] ?? 0,
                [.. (record["arguments"]?.AsArray() ?? []).Select(a => (string?)a ?? string.Empty)],
                (record["environment"]?.AsObject() ?? []).ToDictionary(e => e.Key, e => (string?)e.Value ?? string.Empty),
                (string?)record["workingDirectory"] ?? string.Empty,
                (string?)record["input"] ?? string.Empty,
                (string?)record["persona"]);
        }
    }
}
