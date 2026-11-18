using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

/// <summary>The decider records a title or a field value exactly at its cap and refuses one character, or one item, over it.</summary>
public sealed class ParcelCapsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly ParcelType Task = SoftwareTypes.Task;

    private static Parcel NewTask() => Parcel.Create(ParcelDecider.Create(Task, GateSet.Shipped, "coanda", "Caps", null, "brian", Now));

    private static string Of(int length) => new('a', length);

    private static void Records(string field, FieldValue value) =>
        Assert.Equivalent(value, ParcelDecider.SetField(Task, NewTask(), field, value, "brian", Now).Value, strict: true);

    private static string Refusal(string field, FieldValue value) =>
        Assert.Throws<DomainException>(() => ParcelDecider.SetField(Task, NewTask(), field, value, "brian", Now)).Message;

    [Fact]
    public void The_caps_meet_the_floors_the_planning_records_and_GitHub_set()
    {
        Assert.True(ParcelCaps.TitleLength >= 652);
        Assert.True(ParcelCaps.TextLength >= 65_536);
        Assert.True(ParcelCaps.ItemLength >= 3_475);
        Assert.True(ParcelCaps.ItemCount >= 18);
    }

    [Fact]
    public void A_title_at_its_cap_is_recorded_and_one_character_over_it_is_refused_naming_the_cap_and_the_length()
    {
        // the cap measures the title as it is recorded, after trimming
        Assert.Equal(Of(ParcelCaps.TitleLength), ParcelDecider.Create(Task, GateSet.Shipped, "coanda", $"  {Of(ParcelCaps.TitleLength)}  ", null, "brian", Now).Title);

        DomainException refused = Assert.Throws<DomainException>(
            () => ParcelDecider.Create(Task, GateSet.Shipped, "coanda", Of(ParcelCaps.TitleLength + 1), null, "brian", Now));
        Assert.Equal("A parcel's title holds at most 1000 characters, and the title given is 1001 characters long.", refused.Message);
    }

    [Fact]
    public void A_text_value_at_its_cap_is_recorded_and_one_character_over_it_is_refused()
    {
        Records("objective", FieldValue.OfText(Of(ParcelCaps.TextLength)));

        Assert.Equal(
            "Field 'objective' takes a text value of at most 65536 characters, and the one given is 65537 characters long.",
            Refusal("objective", FieldValue.OfText(Of(ParcelCaps.TextLength + 1))));
    }

    [Fact]
    public void A_list_item_at_its_cap_is_recorded_and_one_character_over_it_is_refused()
    {
        Records("criteria", FieldValue.OfItems("short", Of(ParcelCaps.ItemLength)));

        Assert.Equal(
            "Field 'criteria' takes list items of at most 8000 characters, and the one given is 8001 characters long.",
            Refusal("criteria", FieldValue.OfItems("short", Of(ParcelCaps.ItemLength + 1))));
    }

    [Fact]
    public void A_list_at_its_item_count_cap_is_recorded_and_one_item_over_it_is_refused()
    {
        Records("criteria", FieldValue.OfItems([.. Enumerable.Repeat("x", ParcelCaps.ItemCount)]));

        Assert.Equal(
            "Field 'criteria' holds a list of at most 200 items, and the list given holds 201.",
            Refusal("criteria", FieldValue.OfItems([.. Enumerable.Repeat("x", ParcelCaps.ItemCount + 1)])));
    }

    [Fact]
    public void A_reference_id_at_its_cap_is_recorded_and_one_character_over_it_is_refused()
    {
        Records("pull-request", FieldValue.OfReference(Of(ParcelCaps.ReferenceIdLength)));

        Assert.Equal(
            "Field 'pull-request' takes a reference id of at most 1000 characters, and the one given is 1001 characters long.",
            Refusal("pull-request", FieldValue.OfReference(Of(ParcelCaps.ReferenceIdLength + 1))));
    }

    [Fact]
    public void A_reference_url_at_its_cap_is_recorded_and_one_character_over_it_is_refused()
    {
        Records("pull-request", FieldValue.OfReference("42", Of(ParcelCaps.ReferenceUrlLength)));

        Assert.Equal(
            "Field 'pull-request' takes a reference URL of at most 2048 characters, and the one given is 2049 characters long.",
            Refusal("pull-request", FieldValue.OfReference("42", Of(ParcelCaps.ReferenceUrlLength + 1))));
    }
}
