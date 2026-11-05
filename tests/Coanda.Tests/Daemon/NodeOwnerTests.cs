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
}
