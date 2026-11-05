namespace Coanda.Daemon.Modules;

/// <summary>
/// The stored binding of one module token: the SHA-256 hash of the token, never its plain value, and
/// the module it was issued for. It lives in the node tenant, since a module is installed once per
/// node and a presented token does not say which project it is for. No API view returns it.
/// </summary>
public sealed class ModuleTokenRecord
{
    /// <summary>The token's SHA-256 hash, lower-case hex.</summary>
    public string Id { get; set; } = string.Empty;

    public string Module { get; set; } = string.Empty;
}
