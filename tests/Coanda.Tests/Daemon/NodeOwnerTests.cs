using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Daemon;

namespace Coanda.Tests.Daemon;

public sealed class NodeOwnerTests
{
    [Fact]
    public void The_owner_of_a_named_account_takes_that_name_as_its_id()
    {
        NodeOwner owner = NodeOwner.OfAccount("jose");

        Assert.Equal("jose", owner.Id);
        Assert.Equal(new Principal("jose", Role.Owner), owner.Principal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_owner_of_an_account_the_runtime_cannot_name_takes_the_local_owner_id_so_the_daemon_still_starts(string? accountName)
    {
        NodeOwner owner = NodeOwner.OfAccount(accountName);

        Assert.Equal(Principal.LocalOwner.Id, owner.Id);
        Assert.Equal(Role.Owner, owner.Principal.Role);
    }

    [Fact]
    public void A_home_with_no_owner_id_record_names_its_owner_by_the_account_and_one_that_records_an_id_keeps_it()
    {
        using var temp = new TemporaryHome();
        NodeOwner unrecorded = NodeOwner.Of(temp.Home);
        Assert.Equal((NodeOwner.OfAccount().Id, false), (unrecorded.Id, unrecorded.Recorded));

        unrecorded.Record("the-owner");
        Assert.Equal(("the-owner", true), (unrecorded.Id, unrecorded.Recorded));
        Assert.Equal("the-owner", NodeOwnerRecord.Read(temp.Home));
        Assert.Equal("the-owner", NodeOwner.Of(temp.Home).Id);

        // The same id again changes nothing; another is refused, and the record keeps the first.
        unrecorded.Record("the-owner");
        Assert.Null(unrecorded.RefusalOf("the-owner"));
        DomainException refused = Assert.Throws<DomainException>(() => unrecorded.Record("someone-else"));
        Assert.Equal("This node already records the owner id 'the-owner', not 'someone-else'; a node keeps one owner id.", refused.Message);
        Assert.Equal("the-owner", NodeOwnerRecord.Read(temp.Home));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"ownerId\":\" \"}")]
    [InlineData("{\"ownerId\":\"a\",\"other\":1}")]
    [InlineData("not json")]
    public void An_owner_id_record_that_is_not_one_object_holding_one_non_blank_id_is_refused(string content)
    {
        using var temp = new TemporaryHome();
        File.WriteAllText(temp.Home.NodeOwnerPath, content);

        Assert.Contains("is not one JSON object holding one non-blank ownerId", Assert.Throws<DomainException>(() => NodeOwnerRecord.Read(temp.Home)).Message, StringComparison.Ordinal);
    }
}
