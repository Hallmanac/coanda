namespace Coanda.Sdk.Client;

/// <summary>
/// A module's client for the core's loopback API. It takes its <see cref="HttpClient"/> from the
/// caller, so a host registers it as a typed client, and presents the module token on every call in
/// <see cref="CoreApi.TokenHeader"/>. It offers a typed call only for an endpoint the core has;
/// further calls arrive with the work that uses them.
/// </summary>
public sealed class CoreApiClient(HttpClient http, CoreApiOptions options)
{
    /// <summary>Whether the core answers its health endpoint with success; a core that cannot be reached or does not answer in the client's time is not healthy.</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Request(HttpMethod.Get, "/health");
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout ran out, which a core that does not answer looks like.
            return false;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(options.Address, path));
        request.Headers.Add(CoreApi.TokenHeader, options.Token);
        return request;
    }
}
