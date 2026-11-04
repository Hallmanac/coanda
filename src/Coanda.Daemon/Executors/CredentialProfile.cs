namespace Coanda.Daemon.Executors;

/// <summary>
/// A named credential profile a node holds for one vendor, which a spawn runs under. For Claude
/// Code it holds exactly one of a config directory, always an absolute path, or an API key helper
/// command. Neither is a key: the directory is the vendor's own login state, and the helper prints
/// a key it obtains from elsewhere when the vendor runs it.
/// </summary>
public sealed record CredentialProfile(string Vendor, string Name, string? ConfigDirectory, string? ApiKeyHelper);
