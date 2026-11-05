using Coanda.Sdk.Contract;

namespace Coanda.Tests.Sdk.Contract;

/// <summary>
/// Each schema document and the SDK contract type that is it, and for each document an example
/// that carries every property the document defines at every level the schema closes; an answer
/// with several kinds has one example per kind. The examples write a date with its offset and a
/// uuid in lowercase, the way the SDK writes them, so a round trip can be compared as JSON.
/// </summary>
public static class ContractDocuments
{
    public static IReadOnlyDictionary<string, Type> Types { get; } = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["describe.schema.json"] = typeof(DescribeAnswer),
        ["dispatch.schema.json#/definitions/request"] = typeof(DispatchRequest),
        ["dispatch.schema.json#/definitions/answer"] = typeof(DispatchAnswer),
        ["status.schema.json#/definitions/request"] = typeof(StatusRequest),
        ["status.schema.json#/definitions/answer"] = typeof(StatusAnswer),
        ["stop.schema.json"] = typeof(StopRequest),
        ["feed-delivery.schema.json"] = typeof(FeedDelivery),
        ["watch-request.schema.json"] = typeof(WatchRequest),
        ["recorded-event.schema.json"] = typeof(RecordedEvent),
        ["manifest.schema.json"] = typeof(ModuleManifest),
        ["approval.schema.json"] = typeof(ApprovalRecord),
    };

    /// <summary>Every example, as its document's address, a name for it, and its JSON.</summary>
    public static IReadOnlyList<(string Address, string Name, string Json)> Examples { get; } =
    [
        ("describe.schema.json", "describe", """
            {
              "moduleApiVersion": 1,
              "version": "1.4.0",
              "steps": [
                {
                  "name": "software/gates",
                  "argumentSchema": { "type": "object", "properties": { "limit": { "type": "integer" } } },
                  "resultSchema": { "type": "object", "required": ["verdict"] }
                }
              ],
              "eventKinds": [
                { "kind": "software/review-requested", "watchableFields": ["repo", "reviewer"] }
              ],
              "subscriptions": ["parcel-entered", "flow-run-ended", "flow-run-stopped", "flow-run-paused", "parcel-field-set"],
              "settingsSchema": { "type": "object", "properties": { "primaryBranch": { "type": "string" } } }
            }
            """),
        ("dispatch.schema.json#/definitions/request", "dispatch request", """
            {
              "project": "coanda",
              "parcelId": "019a4f3e-0c1d-7a2b-9c3d-4e5f60718293",
              "flowRunId": "019a4f3e-0c1d-7a2b-9c3d-4e5f60718294",
              "step": "software/gates",
              "visit": 2,
              "dispatchId": "019a4f3e-0c1d-7a2b-9c3d-4e5f60718295",
              "resumed": false,
              "workPath": "/home/someone/.coanda/flow-runs/019a4f3e-0c1d-7a2b-9c3d-4e5f60718294",
              "arguments": { "limit": 30, "commands": ["build", "test"], "nested": { "deep": true } }
            }
            """),
        ("dispatch.schema.json#/definitions/answer", "dispatch done", """
            { "answer": "done", "result": { "verdict": "failed", "reason": "the tests failed", "lines": ["one", "two"] } }
            """),
        ("dispatch.schema.json#/definitions/answer", "dispatch paused", """
            { "answer": "paused", "reason": "No GitHub profile is selected for this project on this node.", "lever": "coa module profile select" }
            """),
        ("dispatch.schema.json#/definitions/answer", "dispatch running", """
            { "answer": "running" }
            """),
        ("status.schema.json#/definitions/request", "status request", """
            { "dispatchId": "019a4f3e-0c1d-7a2b-9c3d-4e5f60718295" }
            """),
        ("status.schema.json#/definitions/answer", "status running", """
            { "answer": "running", "progress": "waiting on 2 of 5 checks" }
            """),
        ("status.schema.json#/definitions/answer", "status done", """
            { "answer": "done", "result": { "verdict": "done", "pullRequest": 12 } }
            """),
        ("status.schema.json#/definitions/answer", "status paused", """
            { "answer": "paused", "reason": "The merge found the head moved.", "lever": "coa flow-run resume" }
            """),
        ("status.schema.json#/definitions/answer", "status unknown", """
            { "answer": "unknown" }
            """),
        ("stop.schema.json", "stop request", """
            { "dispatchId": "019a4f3e-0c1d-7a2b-9c3d-4e5f60718295" }
            """),
        ("feed-delivery.schema.json", "feed delivery", """
            {
              "events": [
                { "kind": "parcel-entered", "project": "coanda", "sequence": 41, "parcelId": "019a4f3e-0c1d-7a2b-9c3d-4e5f60718293", "stage": "review" },
                { "kind": "flow-run-ended", "project": "coanda", "sequence": 42, "flowRunId": "019a4f3e-0c1d-7a2b-9c3d-4e5f60718294" },
                { "kind": "flow-run-stopped", "project": "coanda", "sequence": 43, "flowRunId": "019a4f3e-0c1d-7a2b-9c3d-4e5f60718296" },
                { "kind": "flow-run-paused", "project": "other", "sequence": 44, "flowRunId": "019a4f3e-0c1d-7a2b-9c3d-4e5f60718297" },
                { "kind": "parcel-field-set", "project": "coanda", "sequence": 45, "parcelId": "019a4f3e-0c1d-7a2b-9c3d-4e5f60718293", "field": "title" }
              ]
            }
            """),
        ("watch-request.schema.json", "watch request", """
            {
              "project": "coanda",
              "watches": [
                { "kind": "software/review-requested", "filters": [{ "field": "reviewer", "value": "someone" }, { "field": "repo", "value": "coanda" }] }
              ]
            }
            """),
        ("recorded-event.schema.json", "recorded event", """
            {
              "project": "coanda",
              "kind": "software/review-requested",
              "externalId": "github.com/Hallmanac/coanda/pull/12/review-request/3",
              "parcelId": "019a4f3e-0c1d-7a2b-9c3d-4e5f60718293",
              "fields": { "repo": "coanda", "reviewer": "someone", "pullRequest": "12" }
            }
            """),
        ("manifest.schema.json", "manifest", """
            {
              "name": "software",
              "version": "1.4.0",
              "moduleApiVersion": 1,
              "start": ["bin/coanda-software", "--quiet"],
              "sourceUrl": "https://github.com/Hallmanac/coanda",
              "sourceCommit": "70e9303c4b1f0d5e6a7b8c9d0e1f2a3b4c5d6e7f"
            }
            """),
        ("approval.schema.json", "approval", """
            {
              "module": "software",
              "version": "1.4.0",
              "artifactHash": "sha256:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
              "moduleApiVersion": 1,
              "sourceUrl": "https://github.com/Hallmanac/coanda",
              "sourceCommit": "70e9303c4b1f0d5e6a7b8c9d0e1f2a3b4c5d6e7f",
              "signer": "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
              "issuedAt": "2026-11-04T17:42:00+00:00",
              "reviewId": "review-0001",
              "signature": "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+Pw=="
            }
            """),
    ];

    public static TheoryData<string> ExampleNames()
    {
        var names = new TheoryData<string>();
        foreach ((_, string name, _) in Examples)
        {
            names.Add(name);
        }

        return names;
    }

    public static (string Address, Type Type, string Json) Example(string name)
    {
        (string address, _, string json) = Examples.Single(example => example.Name == name);
        return (address, Types[address], json);
    }
}
