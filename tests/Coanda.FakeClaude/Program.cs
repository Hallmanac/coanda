using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coanda.Testing.FakeClaude;

/// <summary>
/// A stand-in for the claude CLI that the adapter tests start in its place. Its control directory
/// comes from <see cref="ControlVariable"/> in the environment it is started with. Every start
/// appends one record to <c>records.jsonl</c> there, holding its arguments, environment, working
/// directory, standard input, and the persona file as it found it, then plays the entry of
/// <c>script.json</c> whose position is the number of starts before it (the last entry once they
/// run out): a system init event, a rate-limit event, the entry's assistant events, and its result,
/// printed the way Claude Code 2.1.318 prints them in print mode with stream-json output. An entry
/// with <c>waitFor</c> holds its result until a file of that name appears in the control directory.
/// Started with <see cref="CommandFlag"/> first, it is instead the stand-in command the command
/// runner's tests start, and needs no control directory (see <see cref="Command"/>).
/// </summary>
public static class Program
{
    public const string ControlVariable = "FAKE_CLAUDE_DIR";
    public const string ChildFlag = "--fake-child";
    public const string WritingChildFlag = "--fake-writing-child";
    public const string IgnoreInputVariable = "FAKE_CLAUDE_IGNORE_INPUT";
    public const string CommandFlag = "--fake-command";

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static int Main(string[] args)
    {
        if (args is [ChildFlag, string pidFile])
        {
            WritePid(pidFile);
            Thread.Sleep(TimeSpan.FromSeconds(120));
            return 0;
        }

        if (args is [WritingChildFlag, string writerPidFile])
        {
            File.WriteAllText(writerPidFile, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return KeepWriting();
        }

        if (args is [CommandFlag, .. string[] command])
        {
            return Command(command);
        }

        string? control = Environment.GetEnvironmentVariable(ControlVariable);
        if (string.IsNullOrEmpty(control))
        {
            Console.Error.WriteLine($"{ControlVariable} is not set.");
            return 3;
        }

        if (Environment.GetEnvironmentVariable(IgnoreInputVariable) == "1")
        {
            // Never reads its input, so a writer of a large prompt blocks until it gives up.
            WritePid(Path.Combine(control, "stalled.pid"));
            Thread.Sleep(TimeSpan.FromSeconds(60));
            return 0;
        }

        string input = Console.In.ReadToEnd();
        int ordinal = Record(control, args, input);
        JsonArray script = JsonNode.Parse(File.ReadAllText(Path.Combine(control, "script.json")))?.AsArray() ?? [];
        JsonObject entry = script.Count == 0 ? [] : script[Math.Min(ordinal, script.Count - 1)]?.AsObject() ?? [];
        return Play(control, args, entry);
    }

    /// <summary>
    /// The stand-in command: <c>exit &lt;code&gt; &lt;stdout&gt; &lt;stderr&gt;</c> writes the two texts and
    /// exits with the code; <c>stdin</c> reads its standard input to the end and prints how many
    /// characters it held; <c>args ...</c> prints each argument it was given in brackets, one a line;
    /// <c>leave-child &lt;pid file&gt;</c> starts a child that shares its output and outlives it, then
    /// exits; <c>leave-writer &lt;pid file&gt;</c> does the same with a child that writes a line to the
    /// shared output every 20 milliseconds until a write fails or two minutes pass; <c>hold &lt;pid file&gt;</c> writes its own pid beside the file, with <c>.parent</c> added
    /// to its name, starts such a child, and then waits two minutes. The child writes its pid to the file.
    /// <c>flood &lt;count&gt;</c> writes the lines <c>line 1</c> to <c>line &lt;count&gt;</c> and exits.
    /// </summary>
    private static int Command(string[] command)
    {
        switch (command)
        {
            case ["exit", string code, string output, string error]:
                Console.Out.Write(output);
                Console.Out.Flush();
                Console.Error.Write(error);
                return int.Parse(code, System.Globalization.CultureInfo.InvariantCulture);
            case ["stdin"]:
                Console.Out.WriteLine($"stdin:{Console.In.ReadToEnd().Length}");
                return 0;
            case ["args", .. string[] given]:
                foreach (string argument in given)
                {
                    Console.Out.WriteLine($"[{argument}]");
                }

                return 0;
            case ["leave-child", string pidFile]:
                StartChild(pidFile);
                Console.Out.WriteLine("parent exits");
                return 0;
            case ["leave-writer", string pidFile]:
                StartChild(pidFile, WritingChildFlag);
                Console.Out.WriteLine("parent exits");
                return 0;
            case ["flood", string count]:
                int lines = int.Parse(count, System.Globalization.CultureInfo.InvariantCulture);
                for (int written = 1; written <= lines; written++)
                {
                    Console.Out.Write("line ");
                    Console.Out.WriteLine(written);
                }

                return 0;
            case ["hold", string pidFile]:
                File.WriteAllText(pidFile + ".parent", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                StartChild(pidFile);
                Console.Out.WriteLine("holding");
                Console.Out.Flush();
                Thread.Sleep(TimeSpan.FromSeconds(120));
                return 0;
            default:
                Console.Error.WriteLine($"No such fake command: {string.Join(' ', command)}");
                return 2;
        }
    }

    /// <summary>Starts a child that inherits this process's standard handles, and waits for it to write its pid.</summary>
    private static void StartChild(string pidFile, string flag = ChildFlag)
    {
        var child = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("No process path."))
        {
            UseShellExecute = false,
        };
        child.ArgumentList.Add(flag);
        child.ArgumentList.Add(pidFile);
        Process.Start(child);
        for (int waited = 0; waited < 200 && !File.Exists(pidFile); waited++)
        {
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Writes to standard output until the reader of it goes away, then exits. It writes to the
    /// descriptor directly, because the console's own stream ignores a broken pipe; that makes it
    /// a Unix-only mode.
    /// </summary>
    private static int KeepWriting()
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        byte[] line = "still writing\n"u8.ToArray();
        try
        {
            using var output = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(1, ownsHandle: false), FileAccess.Write, bufferSize: 0);
            while (elapsed.Elapsed < TimeSpan.FromSeconds(120))
            {
                output.Write(line);
                Thread.Sleep(20);
            }
        }
        catch (IOException)
        {
            return 0;
        }

        return 0;
    }

    /// <summary>Appends this start's record and returns how many starts came before it.</summary>
    private static int Record(string control, string[] args, string input)
    {
        var environment = new JsonObject();
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            environment[(string)variable.Key] = (string?)variable.Value;
        }

        string persona = Path.Combine(Directory.GetCurrentDirectory(), ".coanda", "persona.md");
        var record = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["arguments"] = new JsonArray([.. args.Select(a => (JsonNode?)JsonValue.Create(a))]),
            ["environment"] = environment,
            ["workingDirectory"] = Directory.GetCurrentDirectory(),
            ["input"] = input,
            ["persona"] = File.Exists(persona) ? File.ReadAllText(persona) : null,
        };
        string path = Path.Combine(control, "records.jsonl");
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                using var reader = new StreamReader(stream, leaveOpen: true);
                int before = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                stream.Seek(0, SeekOrigin.End);
                using var writer = new StreamWriter(stream);
                writer.Write(record.ToJsonString(Compact) + "\n");
                return before;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static int Play(string control, string[] args, JsonObject entry)
    {
        string session = After(args, "--session-id") ?? After(args, "--resume") ?? "no-session";
        if ((string?)entry["holdInit"] is { } held)
        {
            WaitFor(Path.Combine(control, held));
        }

        if ((string?)entry["settings"] != "skipped")
        {
            SessionStartHooks(args, session);
        }

        if ((bool?)entry["init"] != false)
        {
            Print(new JsonObject
            {
                ["type"] = "system",
                ["subtype"] = "init",
                ["cwd"] = Directory.GetCurrentDirectory(),
                ["session_id"] = session,
                ["tools"] = new JsonArray([.. (After(args, "--tools") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => (JsonNode?)JsonValue.Create(t))]),
                ["mcp_servers"] = new JsonArray(),
                ["model"] = After(args, "--model"),
                ["permissionMode"] = After(args, "--permission-mode"),
                ["apiKeySource"] = (string?)entry["apiKeySource"] ?? "none",
                ["claude_code_version"] = (string?)entry["version"] ?? "2.1.318",
            });
            Print(new JsonObject
            {
                ["type"] = "rate_limit_event",
                ["rate_limit_info"] = new JsonObject { ["status"] = "allowed", ["rateLimitType"] = "five_hour" },
                ["session_id"] = session,
            });
        }

        if (entry["rewrite"] is JsonObject rewrite)
        {
            File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), (string?)rewrite["path"] ?? "rewritten.txt"), (string?)rewrite["content"] ?? string.Empty);
        }

        if ((bool?)entry["child"] == true)
        {
            var child = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("No process path."))
            {
                UseShellExecute = false,
            };
            child.ArgumentList.Add(ChildFlag);
            child.ArgumentList.Add(Path.Combine(control, "child.pid"));
            Process.Start(child);
        }

        foreach (JsonNode? message in entry["messages"]?.AsArray() ?? [])
        {
            Print(new JsonObject
            {
                ["type"] = "assistant",
                ["message"] = new JsonObject
                {
                    ["id"] = (string?)message?["id"],
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = After(args, "--model"),
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = (string?)message?["text"] ?? "working" }),
                    ["usage"] = Usage(message),
                },
                ["parent_tool_use_id"] = null,
                ["session_id"] = session,
            });
        }

        foreach (JsonNode? line in entry["stderr"]?.AsArray() ?? [])
        {
            Console.Error.WriteLine((string?)line);
        }

        Console.Error.Flush();
        if ((string?)entry["waitFor"] is { } release)
        {
            // Holds the session in progress until the test writes the release file, as a long turn would.
            WaitFor(Path.Combine(control, release));
        }

        int exitCode = (int?)entry["exitCode"] ?? 0;
        switch ((string?)entry["result"] ?? "success")
        {
            case "none":
                return exitCode;
            case "hang":
                Thread.Sleep(TimeSpan.FromSeconds(120));
                return exitCode;
        }

        bool isError = (string?)entry["result"] == "error";
        var result = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = (string?)entry["subtype"] ?? (isError ? "error_during_execution" : "success"),
            ["is_error"] = isError,
            ["duration_ms"] = (long?)entry["durationMs"] ?? 1000,
            ["num_turns"] = (int?)entry["turns"] ?? 1,
            ["result"] = entry["reply"]?.DeepClone(),
            ["session_id"] = session,
            ["total_cost_usd"] = entry["totalCost"]?.DeepClone() ?? 0,
            ["usage"] = Usage(entry["usage"]),
        };
        if (entry["errors"] is JsonArray errors)
        {
            result["errors"] = errors.DeepClone();
        }

        if (entry["sinceLast"] is JsonObject sinceLast)
        {
            var models = new JsonObject();
            foreach ((string model, JsonNode? figures) in sinceLast)
            {
                models[model] = new JsonObject
                {
                    ["inputTokens"] = (long?)figures?["input"] ?? 0,
                    ["outputTokens"] = (long?)figures?["output"] ?? 0,
                    ["cacheReadInputTokens"] = (long?)figures?["cacheRead"] ?? 0,
                    ["cacheCreationInputTokens"] = (long?)figures?["cacheCreation"] ?? 0,
                    ["webSearchRequests"] = 0,
                    ["costUSD"] = figures?["cost"]?.DeepClone() ?? 0,
                };
            }

            result["model_usage_since_last_result"] = models;
        }

        Print(result);
        return exitCode;
    }

    /// <summary>Waits until the file exists, or two minutes pass.</summary>
    private static void WaitFor(string path)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(120);
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// Prints what Claude Code prints for each session-start hook the <c>--settings</c> document
    /// registers before its init event: a <c>hook_started</c> and a <c>hook_response</c> event, named
    /// for a startup or a resume. A hook that runs <c>coa settings-evidence &lt;marker&gt;</c> is quoted
    /// as having printed the marker on its standard error, as <c>coa</c> does; any other prints nothing.
    /// The fake runs no hook itself.
    /// </summary>
    private static void SessionStartHooks(string[] args, string session)
    {
        if (After(args, "--settings") is not { } document || JsonNode.Parse(document)?["hooks"]?["SessionStart"] is not JsonArray groups)
        {
            return;
        }

        string name = After(args, "--resume") is null ? "SessionStart:startup" : "SessionStart:resume";
        foreach (JsonNode? hook in groups.SelectMany(group => group?["hooks"]?.AsArray() ?? []))
        {
            if ((string?)hook?["type"] != "command")
            {
                continue;
            }

            string[] arguments = [.. (hook?["args"]?.AsArray() ?? []).Select(a => (string?)a ?? string.Empty)];
            string stderr = arguments is ["settings-evidence", string marker] ? marker + "\n" : string.Empty;
            string id = Guid.NewGuid().ToString();
            Print(new JsonObject
            {
                ["type"] = "system",
                ["subtype"] = "hook_started",
                ["hook_id"] = id,
                ["hook_name"] = name,
                ["hook_event"] = "SessionStart",
                ["session_id"] = session,
            });
            Print(new JsonObject
            {
                ["type"] = "system",
                ["subtype"] = "hook_response",
                ["hook_id"] = id,
                ["hook_name"] = name,
                ["hook_event"] = "SessionStart",
                ["output"] = stderr,
                ["stdout"] = string.Empty,
                ["stderr"] = stderr,
                ["exit_code"] = 0,
                ["outcome"] = "success",
                ["session_id"] = session,
            });
        }
    }

    private static JsonObject Usage(JsonNode? figures) => new()
    {
        ["input_tokens"] = (long?)figures?["input"] ?? 0,
        ["cache_creation_input_tokens"] = (long?)figures?["cacheCreation"] ?? 0,
        ["cache_read_input_tokens"] = (long?)figures?["cacheRead"] ?? 0,
        ["output_tokens"] = (long?)figures?["output"] ?? 0,
        ["service_tier"] = "standard",
    };

    private static string? After(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Writes this process's pid to a sibling and moves it into place, so a poller never opens the file while it is still being written.</summary>
    private static void WritePid(string path)
    {
        string partial = path + ".tmp";
        File.WriteAllText(partial, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(partial, path, overwrite: true);
    }

    private static void Print(JsonObject line)
    {
        Console.Out.Write(line.ToJsonString(Compact) + "\n");
        Console.Out.Flush();
    }
}
