using System.Text;

namespace Coanda.Sdk.Client;

/// <summary>
/// Where the core's loopback API is and the module token to present to it. A host passes them
/// explicitly; a module the daemon started builds them from its environment with
/// <see cref="FromEnvironment"/>.
/// </summary>
public sealed record CoreApiOptions
{
    public CoreApiOptions(Uri address, string token)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!address.IsAbsoluteUri || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"The core's address must be an absolute http or https URL, not '{address}'.", nameof(address));
        }

        Address = address;
        Token = token;
    }

    /// <summary>The core's address, an absolute http or https URL.</summary>
    public Uri Address { get; }

    /// <summary>The module token, presented on every call. <see cref="object.ToString"/> leaves it out.</summary>
    public string Token { get; }

    /// <summary>
    /// The options the process environment holds in <see cref="CoreApi.AddressVariable"/> and
    /// <see cref="CoreApi.TokenVariable"/>; either missing or malformed throws, naming the variable.
    /// </summary>
    public static CoreApiOptions FromEnvironment() => FromVariables(Environment.GetEnvironmentVariable);

    /// <summary>The options <paramref name="read"/> returns for <see cref="CoreApi.AddressVariable"/> and <see cref="CoreApi.TokenVariable"/>.</summary>
    public static CoreApiOptions FromVariables(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        string address = Require(read, CoreApi.AddressVariable);
        string token = Require(read, CoreApi.TokenVariable);
        return Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? new CoreApiOptions(uri, token)
            : throw new InvalidOperationException($"{CoreApi.AddressVariable} holds '{address}', which is not an absolute http or https URL.");
    }

    /// <summary>
    /// Writes the address and leaves the token out, so options that reach a log or an exception
    /// message do not carry the token that calls the core as the module.
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Address = ").Append(Address).Append(", Token = (redacted)");
        return true;
    }

    private static string Require(Func<string, string?> read, string variable) =>
        read(variable) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{variable} is not set, so the core's API cannot be reached.");
}
