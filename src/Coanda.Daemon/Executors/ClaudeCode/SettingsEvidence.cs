namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// The evidence a session's process prints when the claude CLI applied the inline settings document
/// the adapter passes it. The document registers a <c>SessionStart</c> hook that runs
/// <c>coa</c> with <see cref="Arguments"/> and no shell, in the same exec form as the tool guard's
/// entry, so a CLI that drops one entry drops both. <c>coa</c> prints <see cref="Marker"/> on its
/// standard error and nothing on its standard output: the CLI quotes a hook's standard error in the
/// <c>hook_response</c> event it prints on its own output stream before its init event, and adds
/// only standard output to the session's context. So the marker, matched in a
/// <c>hook_response</c> to a <c>SessionStart</c> hook before the init event, shows that the document
/// was applied and that <c>coa</c> itself started; a <c>SessionStart</c> hook that another settings
/// source registers prints no such line.
/// </summary>
/// <remarks>
/// Confirmed against Claude Code 2.1.318: in stream-json mode it prints <c>hook_started</c> and
/// <c>hook_response</c> system events for a document's <c>SessionStart</c> hook before its init
/// event, named <c>SessionStart:startup</c> on a spawn and <c>SessionStart:resume</c> on a resume,
/// and prints neither when it skips the document. It prints no warning in that mode when it skips
/// one, so the absence of a warning is never evidence.
/// </remarks>
public static class SettingsEvidence
{
    /// <summary>The hidden <c>coa</c> subcommand the document's <c>SessionStart</c> hook runs.</summary>
    public const string Command = "settings-evidence";

    /// <summary>The line the hook prints on its standard error.</summary>
    public const string Marker = "coanda-settings-document-applied";

    /// <summary>The hook event the evidence is printed for.</summary>
    public const string HookEvent = "SessionStart";

    /// <summary>The argument list the hook passes to <c>coa</c>: the subcommand, then the marker it prints.</summary>
    public static IReadOnlyList<string> Arguments { get; } = [Command, Marker];

    /// <summary>
    /// What <c>coa settings-evidence</c> does: given exactly the marker, it writes it as one line to
    /// <paramref name="error"/> and succeeds; given anything else, it writes nothing and fails, so no
    /// other argument list prints the evidence.
    /// </summary>
    public static int Run(IReadOnlyList<string> arguments, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(error);
        if (arguments is not [Command, Marker])
        {
            return 1;
        }

        error.Write(Marker + "\n");
        error.Flush();
        return 0;
    }

    /// <summary>Whether the hook's quoted standard error holds the marker as one of its lines.</summary>
    public static bool Holds(string? standardError) =>
        standardError is not null
        && standardError.Split('\n').Any(line => string.Equals(line.TrimEnd('\r'), Marker, StringComparison.Ordinal));
}
