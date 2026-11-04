using Coanda.Cli.Commands;
using Coanda.Core.Parcels;
using Coanda.Core.Types;
using Coanda.Daemon.Api.Endpoints;

namespace Coanda.Tests.Cli;

public sealed class ParcelRenderingTests
{
    [Fact]
    public void Each_kind_of_value_is_built_from_its_own_options()
    {
        new ParcelSetSettings { Id = "x", Field = "objective", Text = "Ship it." }.ToValue()
            .Should().BeEquivalentTo(FieldValue.OfText("Ship it."));
        new ParcelSetSettings { Id = "x", Field = "criteria", Items = ["one", "two"] }.ToValue()
            .Should().BeEquivalentTo(FieldValue.OfItems("one", "two"));
        new ParcelSetSettings { Id = "x", Field = "pull-request", Reference = "42" }.ToValue()
            .Should().BeEquivalentTo(FieldValue.OfReference("42"));
        new ParcelSetSettings { Id = "x", Field = "pull-request", Reference = "42", Url = "https://example.com/pull/42" }.ToValue()
            .Should().BeEquivalentTo(FieldValue.OfReference("42", "https://example.com/pull/42"));
        new ParcelSetSettings { Id = "x", Field = "objective", Text = "a", Items = ["b"] }.ToValue().Kind
            .Should().BeNull("two shapes are sent as given, for the daemon to refuse");
    }

    [Fact]
    public void Show_prints_every_declared_field_in_order_with_its_value_or_not_set_and_escapes_markup()
    {
        var parcel = new ParcelView(
            Guid.Empty, "coanda", "task", "0123456789abcdef", "refine", "x", "brian", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, null,
            [
                new ParcelFieldView("objective", FieldKinds.Text, FieldValue.OfText("Use [bold] as text")),
                new ParcelFieldView("criteria", FieldKinds.TextList, null),
            ]);

        ParcelRendering.FieldRows(parcel).Should().BeEquivalentTo(
            new[] { new[] { "objective", "Use [[bold]] as text" }, new[] { "criteria", "not set" } },
            o => o.WithStrictOrdering());
    }
}
