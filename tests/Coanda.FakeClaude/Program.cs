using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coanda.Testing.FakeClaude;

/// <summary>
/// A stand-in for the claude CLI that the adapter tests start in its place. Its control directory
/// comes from <see cref="ControlVariable"/> in the environment it is started with. Every start
/// appends one record to <c>records.jsonl</c> there, holding its arguments, environment, working
/// directory, and standard input, then plays the entry of <c>script.json</c> whose position is the
/// number of starts before it (the last entry once they run out): a system init event, a rate-limit
/// event, the entry's assistant events, and its result, printed the way Claude Code 2.1.318 prints
/// them in print mode with stream-json output.
/// </summary>
public static class Program
{
    public const string ControlVariable = "FAKE_CLAUDE_DIR";
    public const string ChildFlag = "--fake-child";
    public const string IgnoreInputVariable = "FAKE_CLAUDE_IGNORE_INPUT";

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static int Main(string[] args)
    {
        if (args is [ChildFlag, string pidFile])
        {
            File.WriteAllText(pidFile, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Thread.Sleep(TimeSpan.FromSeconds(120));
            return 0;
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
            File.WriteAllText(Path.Combine(control, "stalled.pid"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Thread.Sleep(TimeSpan.FromSeconds(60));
            return 0;
        }

        string input = Console.In.ReadToEnd();
        int ordinal = Record(control, args, input);
        JsonArray script = JsonNode.Parse(File.ReadAllText(Path.Combine(control, "script.json")))?.AsArray() ?? [];
        JsonObject entry = script.Count == 0 ? [] : script[Math.Min(ordinal, script.Count - 1)]?.AsObject() ?? [];
        return Play(control, args, entry);
    }

    /// <summary>Appends this start's record and returns how many starts came before it.</summary>
    private static int Record(string control, string[] args, string input)
    {
        var environment = new JsonObject();
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            environment[(string)variable.Key] = (string?)variable.Value;
        }

        var record = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["arguments"] = new JsonArray([.. args.Select(a => (JsonNode?)JsonValue.Create(a))]),
            ["environment"] = environment,
            ["workingDirectory"] = Directory.GetCurrentDirectory(),
            ["input"] = input,
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
            ["claude_code_version"] = "2.1.318",
        });
        Print(new JsonObject
        {
            ["type"] = "rate_limit_event",
            ["rate_limit_info"] = new JsonObject { ["status"] = "allowed", ["rateLimitType"] = "five_hour" },
            ["session_id"] = session,
        });

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

    private static void Print(JsonObject line)
    {
        Console.Out.Write(line.ToJsonString(Compact) + "\n");
        Console.Out.Flush();
    }
}
