using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core.Flows;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

/// <summary>Canonical content carries only what a definition declares, never a property derived from it.</summary>
public sealed class CanonicalContentTests
{
    private static readonly string[] Derived = ["IsAgentStep", "IsSystemStep", "Hash", "First"];

    [Fact]
    public void A_shipped_flows_canonical_json_holds_no_derived_property()
    {
        foreach (Flow flow in SoftwareFlows.All)
        {
            string content = Flow.CanonicalJson(flow);

            foreach (string property in Derived)
            {
                // flow '{flow.Name}' writes only what it declares
                Assert.DoesNotContain(property, PropertyNames(content));
            }
        }
    }

    [Fact]
    public void A_shipped_parcel_types_canonical_json_holds_no_hash()
    {
        foreach (ParcelType type in SoftwareTypes.All)
        {
            // type '{type.Name}' writes only what it declares
            Assert.DoesNotContain("Hash", PropertyNames(ParcelType.CanonicalJson(type)));
        }
    }

    [Fact]
    public void A_flow_stored_with_the_derived_step_properties_still_reads_as_before()
    {
        Flow shipped = SoftwareFlows.Build;
        JsonNode node = JsonNode.Parse(Flow.CanonicalJson(shipped)) ?? throw new InvalidOperationException("The flow wrote no content.");
        foreach (JsonObject step in node["Steps"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            step["IsAgentStep"] = !string.IsNullOrWhiteSpace((string?)step["Agent"]);
            step["IsSystemStep"] = !string.IsNullOrWhiteSpace((string?)step["SystemStep"]);
        }

        string stored = node.ToJsonString();
        List<string> names = PropertyNames(stored);
        Assert.Contains("IsAgentStep", names);
        Assert.Contains("IsSystemStep", names);

        Flow read = Flow.FromJson(stored);

        Assert.Equal(shipped.Hash, read.Hash);
        Assert.Equal(shipped.Steps.Select(s => (s.Name, s.IsAgentStep, s.IsSystemStep)), read.Steps.Select(s => (s.Name, s.IsAgentStep, s.IsSystemStep)));
    }

    private static List<string> PropertyNames(string content)
    {
        List<string> names = [];
        Collect(JsonDocument.Parse(content).RootElement, names);
        return names;
    }

    private static void Collect(JsonElement element, List<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    names.Add(property.Name);
                    Collect(property.Value, names);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Collect(item, names);
                }

                break;
        }
    }
}
