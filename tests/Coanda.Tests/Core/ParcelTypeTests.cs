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
            type.Validate(GateSet.None.Names);
            type.Hash.Should().HaveLength(16);
        }

        ShippedTypes.Idea.Hash.Should().NotBe(ShippedTypes.Task.Hash);
        ShippedTypes.Task.Hash.Should().Be(ShippedTypes.Task.Hash, "the hash is a pure function of the definition");
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
    public void Task_lifecycle_allows_exactly_its_moves(string from, string to, bool allowed) =>
        ShippedTypes.Task.AllowsMove(from, to).Should().Be(allowed);

    [Fact]
    public void A_type_without_a_terminal_state_is_refused()
    {
        ParcelType broken = ShippedTypes.Task with { TerminalState = "finished" };
        FluentActions.Invoking(() => broken.Validate(GateSet.None.Names)).Should().Throw<DomainException>().WithMessage("*terminal state*");
    }

    [Fact]
    public void A_type_whose_move_names_an_unknown_state_is_refused()
    {
        ParcelType broken = ShippedTypes.Task with
        {
            Moves = new Dictionary<string, IReadOnlyList<string>> { ["refine"] = ["shipping"] },
        };
        FluentActions.Invoking(() => broken.Validate(GateSet.None.Names)).Should().Throw<DomainException>().WithMessage("*does not have*");
    }
}
