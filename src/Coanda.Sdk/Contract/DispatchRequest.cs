using System.Text.Json;

namespace Coanda.Sdk.Contract;

/// <summary>
/// One system step for a module to do (<c>dispatch.schema.json#/definitions/request</c>). It carries
/// ids and the step's arguments only. <paramref name="Visit"/> counts from 1, and
/// <paramref name="Resumed"/> is true when the flow run was resumed while this visit was its current
/// step. Dispatching again under the same <paramref name="DispatchId"/> must be safe.
/// </summary>
public sealed record DispatchRequest(
    string Project,
    Guid ParcelId,
    Guid FlowRunId,
    string Step,
    int Visit,
    Guid DispatchId,
    bool Resumed,
    string WorkPath,
    IReadOnlyDictionary<string, JsonElement> Arguments);
