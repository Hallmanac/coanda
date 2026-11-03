namespace Coanda.Core;

/// <summary>A rule of the record was broken. The message quotes the rule so a caller can self-correct.</summary>
public sealed class DomainException(string message) : Exception(message);
