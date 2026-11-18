namespace Coanda.Tests.Daemon.Executors;

/// <summary>
/// Reads code spans and fenced code blocks back the way CommonMark does, so a test can show a value
/// stays inside the fence the daemon set around it rather than only that the text matches.
/// </summary>
internal static class CommonMarkFences
{
    /// <summary>
    /// The content of the code span that opens <paramref name="text"/>, asserting the span ends exactly
    /// where the text does, so nothing of the value escaped it.
    /// </summary>
    public static string WholeSpan(string text)
    {
        int open = Run(text, 0);
        Assert.True(open > 0, $"'{text}' opens with no backtick run");
        int at = open;
        while (at < text.Length)
        {
            if (text[at] != '`')
            {
                at++;
                continue;
            }

            int close = Run(text, at);
            if (close == open)
            {
                Assert.True(at + close == text.Length, $"the span in '{text}' closes before the text ends");
                string content = text[open..at];
                return content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.Trim(' ').Length > 0
                    ? content[1..^1]
                    : content;
            }

            at += close;
        }

        Assert.Fail($"the span in '{text}' never closes");
        return string.Empty;
    }

    /// <summary>
    /// The content of the fenced block whose opening fence is <paramref name="lines"/>[<paramref name="start"/>],
    /// and the index of the line after its closing fence.
    /// </summary>
    public static (string Content, int Next) Block(IReadOnlyList<string> lines, int start)
    {
        string opening = lines[start];
        int fence = Run(opening, 0);
        Assert.True(fence >= 3 && fence == opening.Length, $"'{opening}' is no bare opening fence");
        for (int at = start + 1; at < lines.Count; at++)
        {
            string trimmed = lines[at].TrimStart(' ');
            int run = Run(trimmed, 0);
            if (lines[at].Length - trimmed.Length <= 3 && run >= fence && trimmed[run..].Trim(' ').Length == 0)
            {
                return (string.Join('\n', lines.Skip(start + 1).Take(at - start - 1)), at + 1);
            }
        }

        Assert.Fail($"the block opened by '{opening}' never closes");
        return (string.Empty, lines.Count);
    }

    private static int Run(string text, int at)
    {
        int end = at;
        while (end < text.Length && text[end] == '`')
        {
            end++;
        }

        return end - at;
    }
}
