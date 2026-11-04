using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Software;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

public sealed class ParcelTypeTests
{
    [Fact]
    public void Shipped_types_validate_and_hash_distinctly()
    {
        foreach (ParcelType type in ShippedTypes.All)
        {
            type.Validate(GateSet.Shipped.Names);
            Assert.Equal(16, type.Hash.Length);
        }

        Assert.NotEqual(ShippedTypes.Task.Hash, ShippedTypes.Idea.Hash);
        // the hash is a pure function of the definition
        Assert.Equal(ShippedTypes.Task.Hash, ShippedTypes.Task.Hash);
    }

    [Theory]
    [InlineData("refine", "build", true)]
    [InlineData("build", "review", true)]
    [InlineData("review", "build", true)]
    [InlineData("refine", "review", false)]
    [InlineData("build", "refine", false)]
    [InlineData("refine", "paused", true)]
    [InlineData("paused", "review", true)]
    [InlineData("review", "closed", true)]
    [InlineData("closed", "refine", false)]
    [InlineData("refine", "refine", false)]
    [InlineData("refine", "nowhere", false)]
    public void Task_lifecycle_allows_exactly_its_moves(string from, string to, bool allowed) => Assert.Equal(allowed, ShippedTypes.Task.AllowsMove(from, to));

    [Fact]
    public void A_type_without_a_terminal_stage_is_refused()
    {
        ParcelType broken = ShippedTypes.Task with { TerminalStage = "finished" };
        DomainException exception = Assert.Throws<DomainException>(() => broken.Validate(GateSet.Shipped.Names));
        Assert.Contains("terminal stage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_whose_move_names_an_unknown_stage_is_refused()
    {
        ParcelType broken = ShippedTypes.Task with
        {
            Moves = new Dictionary<string, IReadOnlyList<string>> { ["refine"] = ["shipping"] },
        };
        DomainException exception = Assert.Throws<DomainException>(() => broken.Validate(GateSet.Shipped.Names));
        Assert.Contains("does not have", exception.Message, StringComparison.Ordinal);
    }
}
