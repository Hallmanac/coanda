namespace Coanda.Daemon.Api;

/// <summary>The body of every refusal the daemon answers: a missing token, a domain rule, or a missing record.</summary>
public sealed record ErrorView(string Error);
