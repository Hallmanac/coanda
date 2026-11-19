using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coanda.Sdk.Contract;

namespace Coanda.Sdk.Client;

/// <summary>
/// A module's client for the core's loopback API. It takes its <see cref="HttpClient"/> from the
/// caller, so a host registers it as a typed client, and presents the module token on every call in
/// <see cref="CoreApi.TokenHeader"/>. It offers a typed call only for an endpoint the core has;
/// further calls arrive with the work that uses them. A call the core refuses, or a read of something it
/// does not hold, throws <see cref="CoreApiException"/> quoting the core's reason.
/// </summary>
public sealed class CoreApiClient(HttpClient http, CoreApiOptions options)
{
    /// <summary>How the core's API writes and reads its JSON; a value left unset is not written.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

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

    /// <summary>The record of the project <paramref name="project"/>.</summary>
    public Task<CoreProject> ProjectAsync(string project, CancellationToken cancellationToken) =>
        GetAsync<CoreProject>($"/projects/{string.Join('/', project.Split('/').Select(Uri.EscapeDataString))}", cancellationToken);

    /// <summary>The settings of the module <paramref name="module"/> in <paramref name="project"/>, which a module reads only of its own.</summary>
    public Task<CoreModuleSettings> ModuleSettingsAsync(string project, string module, CancellationToken cancellationToken) =>
        GetAsync<CoreModuleSettings>($"/project-modules?project={Uri.EscapeDataString(project)}&module={Uri.EscapeDataString(module)}", cancellationToken);

    /// <summary>The parcel <paramref name="parcelId"/> in <paramref name="project"/>.</summary>
    public Task<CoreParcel> ParcelAsync(string project, Guid parcelId, CancellationToken cancellationToken) =>
        GetAsync<CoreParcel>($"/parcels/{parcelId}?project={Uri.EscapeDataString(project)}", cancellationToken);

    /// <summary>The flow run <paramref name="flowRunId"/> in <paramref name="project"/>, with its steps.</summary>
    public Task<CoreFlowRun> FlowRunAsync(string project, Guid flowRunId, CancellationToken cancellationToken) =>
        GetAsync<CoreFlowRun>($"/flow-runs/{flowRunId}?project={Uri.EscapeDataString(project)}", cancellationToken);

    /// <summary>Every flow run on the parcel <paramref name="parcelId"/> in <paramref name="project"/>, with their steps.</summary>
    public Task<IReadOnlyList<CoreFlowRun>> FlowRunsAsync(string project, Guid parcelId, CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<CoreFlowRun>>($"/flow-runs?project={Uri.EscapeDataString(project)}&parcel={parcelId}", cancellationToken);

    /// <summary>
    /// Every flow run of <paramref name="project"/>, on any of its parcels, oldest first, with their steps:
    /// those started under the flow whose hash is <paramref name="flowHash"/> when it is given, and those
    /// with a step visit whose agent hash is <paramref name="agentHash"/> when it is given, or both when both
    /// are. A hash matches only exactly, case included; one given empty or blank the core refuses.
    /// </summary>
    public Task<IReadOnlyList<CoreFlowRun>> ProjectFlowRunsAsync(string project, string? flowHash, string? agentHash, CancellationToken cancellationToken)
    {
        string path = $"/flow-runs?project={Uri.EscapeDataString(project)}";
        path = flowHash is null ? path : $"{path}&flowHash={Uri.EscapeDataString(flowHash)}";
        path = agentHash is null ? path : $"{path}&agentHash={Uri.EscapeDataString(agentHash)}";
        return GetAsync<IReadOnlyList<CoreFlowRun>>(path, cancellationToken);
    }

    /// <summary>
    /// Records <paramref name="value"/> in the parcel's field <paramref name="field"/> under the dispatch
    /// <paramref name="dispatchId"/> of the flow run <paramref name="flowRunId"/>, which the module holds,
    /// and returns the parcel as the write left it.
    /// </summary>
    public async Task<CoreParcel> SetFieldAsync(
        string project, Guid parcelId, string field, CoreFieldValue value, Guid flowRunId, Guid dispatchId, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Request(HttpMethod.Post, "/parcels/set-field");
        request.Content = JsonContent.Create(
            new SetFieldRequest(project, parcelId, field, value, new DispatchName(flowRunId, dispatchId)), options: Json);
        return await SendAsync<CoreParcel>(request, cancellationToken);
    }

    /// <summary>
    /// Records <paramref name="value"/> in the parcel's field <paramref name="field"/>, one the module owns,
    /// which it sets under no dispatch, and returns the parcel as the write left it.
    /// </summary>
    public async Task<CoreParcel> SetOwnedFieldAsync(string project, Guid parcelId, string field, CoreFieldValue value, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Request(HttpMethod.Post, "/parcels/set-field");
        request.Content = JsonContent.Create(new SetFieldRequest(project, parcelId, field, value, null), options: Json);
        return await SendAsync<CoreParcel>(request, cancellationToken);
    }

    /// <summary>
    /// Records the external event <paramref name="recorded"/>, of a kind prefixed with the module's own
    /// name, in a project the module is switched on for, and returns the event the project's record holds
    /// for its kind and external id: the one this call recorded, or the one an earlier call recorded, whose
    /// fields and parcel id stand whatever this call gave.
    /// </summary>
    public async Task<CoreExternalEvent> RecordEventAsync(RecordedEvent recorded, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Request(HttpMethod.Post, "/events");
        request.Content = JsonContent.Create(recorded, options: Json);
        return await SendAsync<CoreExternalEvent>(request, cancellationToken);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Request(HttpMethod.Get, path);
        return await SendAsync<T>(request, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string reason = await ReasonAsync(response, cancellationToken);
            throw new CoreApiException((int)response.StatusCode, reason);
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new CoreApiException((int)response.StatusCode, $"The core answered {request.Method} {request.RequestUri?.AbsolutePath} with no document.");
    }

    /// <summary>The reason the core gave in its error document, or the status when it gave none.</summary>
    private static async Task<string> ReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            if (JsonSerializer.Deserialize<ErrorDocument>(body, Json)?.Error is { Length: > 0 } error)
            {
                return error;
            }
        }
        catch (JsonException)
        {
            // Not the core's error document; the status says what there is to say.
        }

        return response.StatusCode == HttpStatusCode.Unauthorized
            ? "The core refused the module's token."
            : $"The core answered with status {(int)response.StatusCode} ({response.StatusCode}).";
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(options.Address, path));
        request.Headers.Add(CoreApi.TokenHeader, options.Token);
        return request;
    }

    private sealed record SetFieldRequest(string Project, Guid ParcelId, string Field, CoreFieldValue Value, DispatchName? Dispatch);

    private sealed record DispatchName(Guid FlowRunId, Guid DispatchId);

    private sealed record ErrorDocument(string? Error);
}
