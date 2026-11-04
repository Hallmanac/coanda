using System.Text.Json;

namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// Finds the answer in a session's final reply: the JSON object the reply ends with. A code fence
/// that ends the reply is looked inside, so a document fenced as <c>```json</c> still counts as
/// trailing. A reply that ends in anything other than a JSON object has no answer.
/// </summary>
public static class ClaudeAnswer
{
    private const string Fence = "```";

    public static string? Trailing(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return null;
        }

        string text = reply.TrimEnd();
        if (text.EndsWith(Fence, StringComparison.Ordinal))
        {
            text = Fenced(text) ?? text;
        }

        return text.EndsWith('}') ? Outermost(text) : null;
    }

    /// <summary>The body of the fence that ends the text, or null when the closing fence has no opening one.</summary>
    private static string? Fenced(string text)
    {
        string body = text[..^Fence.Length];
        int opening = body.LastIndexOf(Fence, StringComparison.Ordinal);
        if (opening < 0)
        {
            return null;
        }

        // The opening fence's line may name a language, as in ```json; the body starts on the next line.
        int lineEnd = body.IndexOf('\n', opening);
        return lineEnd < 0
            ? null
            : body[(lineEnd + 1)..].TrimEnd();
    }

    /// <summary>
    /// The longest suffix of the text, starting at an opening brace, that parses as one JSON object.
    /// Starts are tried from the last brace backwards, so the first suffix that parses whole is the
    /// outermost object the text ends with: an inner object's suffix carries its parent's closing brace.
    /// </summary>
    private static string? Outermost(string text)
    {
        for (int start = text.LastIndexOf('{'); start >= 0; start = start == 0 ? -1 : text.LastIndexOf('{', start - 1))
        {
            string candidate = text[start..];
            if (IsObject(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsObject(string candidate)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(candidate);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
