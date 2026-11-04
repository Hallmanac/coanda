using System.Text.Json;
using Coanda.Core.Flows;

namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// What one Claude Code process printed on its stream-json output, read a line at a time: the
/// usage of every assistant message, counted once per message id at the last figure printed for
/// it, and the result event, when the process printed one. A line that is not a JSON object is
/// passed over, since the transcript keeps it either way.
/// </summary>
/// <remarks>
/// An assistant event's usage is a snapshot that can be printed before the message's output is
/// complete, and the same message id can be printed more than once, so the tokens counted from
/// assistant events are a lower bound on what the process used. Only the result event carries a
/// cost, a turn count, and a duration.
/// </remarks>
public sealed class ClaudeStream
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, long> messages = new(StringComparer.Ordinal);
    private ClaudeResult? result;

    public ClaudeResult? Result
    {
        get
        {
            lock (gate)
            {
                return result;
            }
        }
    }

    /// <summary>The tokens of every assistant message, each counted once at its last printed usage.</summary>
    public long MessageTokens
    {
        get
        {
            lock (gate)
            {
                return messages.Values.Sum();
            }
        }
    }

    /// <summary>How many distinct assistant messages the stream printed.</summary>
    public int MessageCount
    {
        get
        {
            lock (gate)
            {
                return messages.Count;
            }
        }
    }

    public void Read(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            switch (Text(root, "type"))
            {
                case "assistant" when root.TryGetProperty("message", out JsonElement message)
                    && message.ValueKind == JsonValueKind.Object
                    && Text(message, "id") is { } id
                    && message.TryGetProperty("usage", out JsonElement usage):
                    lock (gate)
                    {
                        messages[id] = Tokens(usage, "input_tokens", "output_tokens", "cache_creation_input_tokens", "cache_read_input_tokens");
                    }

                    break;
                case "result":
                    ClaudeResult read = ClaudeResult.From(root);
                    lock (gate)
                    {
                        result = read;
                    }

                    break;
            }
        }
        catch (JsonException)
        {
            // Not an event; the transcript holds it as printed.
        }
    }

    internal static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static long Tokens(JsonElement element, params string[] properties) =>
        element.ValueKind == JsonValueKind.Object
            ? properties.Sum(property => element.TryGetProperty(property, out JsonElement value) && value.TryGetInt64(out long count) ? count : 0L)
            : 0L;

    internal static decimal? Dollars(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal amount)
            ? amount
            : null;
}

/// <summary>
/// A Claude Code result event, as far as the adapter reads it. <see cref="SinceLastResult"/> holds
/// the process's own tokens and cost summed over every model in <c>model_usage_since_last_result</c>,
/// or null when the event carries no such field; <see cref="UsageTokens"/> sums the four token
/// figures of its <c>usage</c>, which reads zero for a process its spend cap stopped.
/// <see cref="TotalCostUsd"/> is the session's total, counting every process before a resume.
/// </summary>
public sealed record ClaudeResult(
    bool IsError,
    string? Subtype,
    string? Text,
    IReadOnlyList<string> Errors,
    decimal? TotalCostUsd,
    long UsageTokens,
    (long Tokens, decimal CostUsd)? SinceLastResult,
    int Turns,
    TimeSpan Duration)
{
    /// <summary>
    /// The process's own usage. Its tokens and cost come from <see cref="SinceLastResult"/>; with no
    /// such field, its tokens are <see cref="UsageTokens"/> and its cost is the increase in the
    /// session's total cost since <paramref name="previousTotalCostUsd"/>, the total the previous
    /// collect read. Its turns and duration are the event's own, which count this process alone.
    /// </summary>
    public Measurement Usage(decimal previousTotalCostUsd) => SinceLastResult is var (tokens, cost)
        ? new Measurement(tokens, cost, Turns, Duration)
        : new Measurement(UsageTokens, Math.Max(0m, (TotalCostUsd ?? previousTotalCostUsd) - previousTotalCostUsd), Turns, Duration);

    /// <summary>The error text: the result text, or when that is empty, the errors joined.</summary>
    public string ErrorText => string.IsNullOrWhiteSpace(Text)
        ? string.Join("; ", Errors)
        : Text;

    internal static ClaudeResult From(JsonElement root)
    {
        bool isError = root.TryGetProperty("is_error", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
        IReadOnlyList<string> errors = root.TryGetProperty("errors", out JsonElement list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.GetRawText())]
            : [];
        long usageTokens = root.TryGetProperty("usage", out JsonElement usage)
            ? ClaudeStream.Tokens(usage, "input_tokens", "output_tokens", "cache_creation_input_tokens", "cache_read_input_tokens")
            : 0L;
        (long, decimal)? sinceLast = root.TryGetProperty("model_usage_since_last_result", out JsonElement models) && models.ValueKind == JsonValueKind.Object
            ? models.EnumerateObject().Aggregate(
                (Tokens: 0L, Cost: 0m),
                (sum, model) => (
                    sum.Tokens + ClaudeStream.Tokens(model.Value, "inputTokens", "outputTokens", "cacheCreationInputTokens", "cacheReadInputTokens"),
                    sum.Cost + (model.Value.ValueKind == JsonValueKind.Object ? ClaudeStream.Dollars(model.Value, "costUSD") ?? 0m : 0m)))
            : null;
        int turns = root.TryGetProperty("num_turns", out JsonElement count) && count.TryGetInt32(out int n) ? n : 0;
        long milliseconds = root.TryGetProperty("duration_ms", out JsonElement duration) && duration.TryGetInt64(out long ms) ? ms : 0L;
        return new ClaudeResult(
            isError,
            ClaudeStream.Text(root, "subtype"),
            ClaudeStream.Text(root, "result"),
            errors,
            ClaudeStream.Dollars(root, "total_cost_usd"),
            usageTokens,
            sinceLast,
            turns,
            TimeSpan.FromMilliseconds(milliseconds));
    }
}
