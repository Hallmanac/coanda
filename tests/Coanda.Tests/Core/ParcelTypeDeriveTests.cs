using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

/// <summary>
/// A parcel type declares the names of the types it may derive. A type that declares none hashes as it did
/// before the list existed, a type that declares some hashes by the list, and a list holding a null entry, an
/// entry that is not a plain name, or one name twice regardless of case is refused naming the type and the entry.
/// </summary>
public sealed class ParcelTypeDeriveTests
{
    private static readonly IReadOnlySet<string> Gates = GateSet.Shipped.Names;

    [Fact]
    public void The_shipped_idea_derives_tasks_alone_and_the_task_declares_none_and_keeps_the_hash_it_had_before_the_list()
    {
        Assert.Equal(["task"], SoftwareTypes.Idea.Derives);
        Assert.Empty(SoftwareTypes.Task.Derives);
        Assert.Equal("ba8ba31be0a921be", SoftwareTypes.Idea.Hash);
        Assert.Equal("e6c4447a6bc6586d", PreviousTypes.IdeaBeforeDerive.Hash);
        Assert.Equal("3e16509d2543dbb9", SoftwareTypes.Task.Hash);
    }

    [Fact]
    public void A_type_that_declares_some_hashes_by_the_list_and_its_order()
    {
        ParcelType none = PreviousTypes.IdeaBeforeDerive;
        ParcelType one = none with { Derives = ["task"] };
        ParcelType two = none with { Derives = ["task", "idea"] };
        ParcelType reordered = none with { Derives = ["idea", "task"] };

        Assert.NotEqual(none.Hash, one.Hash);
        Assert.NotEqual(one.Hash, two.Hash);
        Assert.NotEqual(two.Hash, reordered.Hash);
        Assert.Equal(one.Hash, (none with { Derives = ["task"] }).Hash);
        one.Validate(Gates);
        two.Validate(Gates);
    }

    [Fact]
    public void A_list_read_from_content_round_trips_and_content_without_one_reads_as_none()
    {
        ParcelType derives = SoftwareTypes.Idea;
        ParcelType read = ParcelType.FromJson(ParcelType.CanonicalJson(derives));
        Assert.Equal(["task"], read.Derives);
        Assert.Equal(derives.Hash, read.Hash);

        JsonNode node = JsonNode.Parse(ParcelType.CanonicalJson(SoftwareTypes.Idea)) ?? throw new InvalidOperationException("No content.");
        Assert.True(node.AsObject().Remove("Derives"));
        ParcelType earlier = ParcelType.FromJson(node.ToJsonString());
        Assert.Empty(earlier.Derives);
        Assert.Equal(PreviousTypes.IdeaBeforeDerive.Hash, earlier.Hash);
    }

    [Theory]
    [InlineData("""["task", null]""", "Parcel type 'idea' lists a type it may derive that is null; each entry is a parcel type's name.")]
    [InlineData("""["task", "a task"]""", "Parcel type 'idea' lists 'a task' as a type it may derive; a type's name there is plain: letters, digits, hyphens, and underscores, and not empty.")]
    [InlineData("""[""]""", "Parcel type 'idea' lists '' as a type it may derive; a type's name there is plain: letters, digits, hyphens, and underscores, and not empty.")]
    [InlineData("""["task", "idea", "Task"]""", "Parcel type 'idea' lists 'Task' twice as a type it may derive, regardless of case; each type it may derive is listed once.")]
    public void A_list_with_a_null_entry_a_name_that_is_not_plain_or_one_name_twice_is_refused_naming_the_type_and_the_entry(string list, string refusal)
    {
        JsonNode node = JsonNode.Parse(ParcelType.CanonicalJson(SoftwareTypes.Idea)) ?? throw new InvalidOperationException("No content.");
        node["Derives"] = JsonNode.Parse(list);
        ParcelType type = ParcelType.FromJson(node.ToJsonString());

        Assert.Equal(refusal, Assert.Throws<DomainException>(() => type.Validate(Gates)).Message);
    }
}
