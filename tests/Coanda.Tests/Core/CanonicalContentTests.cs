using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core.Contours;
using Coanda.Core.Software;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

/// <summary>Canonical content carries only what a definition declares, never a property derived from it.</summary>
public sealed class CanonicalContentTests
{
    private static readonly string[] Derived = ["IsJudgment", "IsAction", "Hash", "First"];

    [Fact]
    public void A_shipped_contours_canonical_json_holds_no_derived_property()
    {
        foreach (Contour contour in ShippedContours.All)
        {
            string content = Contour.CanonicalJson(contour);

            foreach (string property in Derived)
            {
                PropertyNames(content).Should().NotContain(property, $"contour '{contour.Name}' writes only what it declares");
            }
        }
    }

    [Fact]
    public void A_shipped_parcel_types_canonical_json_holds_no_hash()
    {
        foreach (ParcelType type in ShippedTypes.All)
        {
            PropertyNames(ParcelType.CanonicalJson(type)).Should().NotContain("Hash", $"type '{type.Name}' writes only what it declares");
        }
    }

    [Fact]
    public void A_contour_stored_with_the_derived_step_properties_still_reads_as_before()
    {
        Contour shipped = ShippedContours.Build;
        JsonNode node = JsonNode.Parse(Contour.CanonicalJson(shipped)) ?? throw new InvalidOperationException("The contour wrote no content.");
        foreach (JsonObject step in node["Steps"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            step["IsJudgment"] = !string.IsNullOrWhiteSpace((string?)step["Agent"]);
            step["IsAction"] = !string.IsNullOrWhiteSpace((string?)step["Action"]);
        }

        string stored = node.ToJsonString();
        PropertyNames(stored).Should().Contain(["IsJudgment", "IsAction"]);

        Contour read = Contour.FromJson(stored);

        read.Hash.Should().Be(shipped.Hash);
        read.Steps.Select(s => (s.Name, s.IsJudgment, s.IsAction))
            .Should().Equal(shipped.Steps.Select(s => (s.Name, s.IsJudgment, s.IsAction)));
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
