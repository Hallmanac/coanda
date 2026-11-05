namespace Coanda.Sdk.Client;

/// <summary>
/// The names a module and the core agree on for reaching the core's loopback API: the environment
/// variables the daemon starts a module with, and the header the module's token travels in.
/// </summary>
public static class CoreApi
{
    /// <summary>The variable that holds the core's address, an absolute http URL on the loopback.</summary>
    public const string AddressVariable = "COANDA_CORE_ADDRESS";

    /// <summary>The variable that holds the module token the core issued to the module.</summary>
    public const string TokenVariable = "COANDA_MODULE_TOKEN";

    /// <summary>The header every call presents the token in, the one the daemon's token middleware reads.</summary>
    public const string TokenHeader = "X-Coanda-Token";
}
