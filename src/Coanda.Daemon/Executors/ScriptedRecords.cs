namespace Coanda.Daemon.Executors;

/// <summary>A spawn the <see cref="ScriptedExecutor"/> received, with every value it was given.</summary>
public sealed record ScriptedSpawn(
    SessionHandle Handle,
    string Agent,
    string Context,
    string WorkingDirectory,
    SpawnCaps Caps,
    string AnswerSchema,
    CredentialProfile? CredentialProfile);

/// <summary>A resume or send message the <see cref="ScriptedExecutor"/> received.</summary>
public sealed record ScriptedMessage(SessionHandle Handle, string Message);
