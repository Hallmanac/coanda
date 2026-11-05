using Coanda.Sdk.Client;

namespace Coanda.Daemon.Modules;

/// <summary>
/// The core's address and the module token an in-process module presents to the core's API. The host
/// builds the module before the daemon listens, so the module is handed <see cref="Options"/>, which
/// gives what <see cref="Connect"/> recorded once the daemon is listening; read before that, it throws.
/// A module in a process of its own reads the same two from its environment instead.
/// </summary>
public sealed class ModuleCoreAccess(string module)
{
    private CoreApiOptions? connected;

    /// <summary>Records the address the daemon listens on and the token issued to the module for this start.</summary>
    public void Connect(Uri address, string token) => connected = new CoreApiOptions(address, token);

    /// <summary>The address and token <see cref="Connect"/> recorded.</summary>
    public CoreApiOptions Options() =>
        connected ?? throw new InvalidOperationException($"Module '{module}' called the core before the daemon was listening, so it has no address or token to call it with.");
}
