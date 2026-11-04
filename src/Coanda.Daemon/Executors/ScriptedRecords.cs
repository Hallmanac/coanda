namespace Coanda.Daemon.Executors;

/// <summary>A spawn the <see cref="ScriptedExecutor"/> received, with every value it was given.</summary>
public sealed record ScriptedSpawn(
    SessionHandle Handle,
    string Agent,
    string Context,
    string WorkingDirectory,
    SpawnCaps Caps,
    string AnswerSchema,
    CredentialProfile? CredentialProfile,
    SessionAccess? Access = null);

/// <summary>A resume or send message the <see cref="ScriptedExecutor"/> received.</summary>
public sealed record ScriptedMessage(SessionHandle Handle, string Message);

/// <summary>A reattach the <see cref="ScriptedExecutor"/> answered with a session it held, and the access it was given.</summary>
public sealed record ScriptedReattach(SessionHandle Handle, SessionAccess? Access);
