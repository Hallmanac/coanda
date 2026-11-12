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
        Assert.Equivalent(FieldValue.OfText("Ship it."), new ParcelSetSettings { Id = "x", Field = "objective", Text = "Ship it." }.ToValue(), strict: true);
        Assert.Equivalent(FieldValue.OfItems("one", "two"), new ParcelSetSettings { Id = "x", Field = "criteria", Items = ["one", "two"] }.ToValue(), strict: true);
        Assert.Equivalent(FieldValue.OfReference("42"), new ParcelSetSettings { Id = "x", Field = "pull-request", Reference = "42" }.ToValue(), strict: true);
        Assert.Equivalent(
            FieldValue.OfReference("42", "https://example.com/pull/42"),
            new ParcelSetSettings { Id = "x", Field = "pull-request", Reference = "42", Url = "https://example.com/pull/42" }.ToValue(),
            strict: true);
        // two shapes are sent as given, for the daemon to refuse
        Assert.Null(new ParcelSetSettings { Id = "x", Field = "objective", Text = "a", Items = ["b"] }.ToValue().Kind);
    }

    [Fact]
    public void List_with_nothing_to_show_says_no_open_parcels_and_with_all_says_no_parcels()
    {
        Assert.Equal("No open parcels in coanda.", ParcelListCommand.EmptyLine("coanda", all: false));
        Assert.Equal("No parcels in coanda.", ParcelListCommand.EmptyLine("coanda", all: true));
        Assert.Equal("No parcels in [[x]].", ParcelListCommand.EmptyLine("[x]", all: true));
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

        Assert.Equal([["objective", "Use [[bold]] as text"], ["criteria", "not set"]], ParcelRendering.FieldRows(parcel));
    }
}
