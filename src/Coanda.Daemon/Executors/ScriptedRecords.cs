namespace Coanda.Daemon.Executors;

/// <summary>A spawn the <see cref="ScriptedExecutor"/> received, with every value it was given, the names of the skills it was given among them.</summary>
public sealed record ScriptedSpawn(
    SessionHandle Handle,
    string Agent,
    string Context,
    string WorkingDirectory,
    SpawnCaps Caps,
    string AnswerSchema,
    CredentialProfile? CredentialProfile,
    SessionAccess? Access = null)
{
    /// <summary>The name of each skill the session was given, in the order given; none when it was given none.</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];
}

/// <summary>A resume or send message the <see cref="ScriptedExecutor"/> received.</summary>
public sealed record ScriptedMessage(SessionHandle Handle, string Message);

/// <summary>A reattach the <see cref="ScriptedExecutor"/> answered with a session it held, and the access it was given.</summary>
public sealed record ScriptedReattach(SessionHandle Handle, SessionAccess? Access);
