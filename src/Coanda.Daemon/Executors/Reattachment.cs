namespace Coanda.Daemon.Executors;

/// <summary>
/// What a reattach found for a session launched before a daemon restart: the session still held,
/// as a <see cref="ReattachedSession"/>, or a <see cref="LostSession"/> the executor let go but whose
/// earlier attempts' usage it observed.
/// </summary>
public abstract record Reattachment(SessionHandle Handle);
