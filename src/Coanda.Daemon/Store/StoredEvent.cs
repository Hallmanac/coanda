namespace Coanda.Daemon.Store;

/// <summary>One event as the events table holds it: its stored type name and its JSON data.</summary>
public sealed record StoredEvent(string Type, string Data);
