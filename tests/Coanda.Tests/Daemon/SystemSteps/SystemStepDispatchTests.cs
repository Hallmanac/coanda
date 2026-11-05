using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.SystemSteps;
using Coanda.Sdk.Contract;
using Coanda.Tests.Sdk.Contract;

namespace Coanda.Tests.Daemon.SystemSteps;

/// <summary>The daemon's dispatch of a system step, mapped to the module API's dispatch request.</summary>
public sealed class SystemStepDispatchTests
{
    private const string RequestAddress = "dispatch.schema.json#/definitions/request";

    [Fact]
    public void A_dispatch_maps_to_a_module_api_dispatch_request_that_its_schema_accepts()
    {
        using var temp = new TemporaryHome();
        Flow flow = RunningSystemStep.FlowNamed();
        FlowStep step = flow.Steps[0];
        Guid flowRunId = Ids.New();
        Guid parcelId = Ids.New();
        Guid dispatchId = Ids.New();
        string workPath = temp.Home.FlowRunWorkPath("alpha", flowRunId);
        var dispatch = new SystemStepDispatch(
            FlowRunAdvancer.PrincipalFor(flowRunId), "alpha", flowRunId, parcelId, flow, step,
            Resumed: true, Visit: 2, DispatchId: dispatchId, WorkPath: workPath);

        DispatchRequest request = dispatch.ToContract();
        string json = JsonSerializer.Serialize(request, ModuleJson.Options);

        Assert.Empty(ModuleSchemas.Load(RequestAddress).Validate(json));
        Assert.Equal(
            ("alpha", parcelId, flowRunId, RunningSystemStep.StepName, 2, dispatchId, true, workPath),
            (request.Project, request.ParcelId, request.FlowRunId, request.Step, request.Visit, request.DispatchId, request.Resumed, request.WorkPath));
        Assert.Equal("as the author wrote it", request.Arguments["note"].GetString());
        // the module sees ids and the step's arguments only: no principal, no flow, no flow step
        JsonObject written = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("The request wrote no object.");
        Assert.Equivalent(
            new[] { "project", "parcelId", "flowRunId", "step", "visit", "dispatchId", "resumed", "workPath", "arguments" },
            written.Select(property => property.Key),
            strict: true);
    }
}
