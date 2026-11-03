using Coanda.Core;

namespace Coanda.Tests.Core;

public sealed class IdsTests
{
    [Fact]
    public void Short_ids_of_records_created_in_the_same_moment_differ()
    {
        List<string> shorts = Enumerable.Range(0, 50).Select(_ => Ids.Short(Ids.New())).ToList();
        shorts.Should().OnlyHaveUniqueItems("the short form is the random tail, not the timestamp head");
        shorts.Should().AllSatisfy(s => s.Should().HaveLength(8));
    }

    [Fact]
    public void A_fragment_matches_by_suffix_or_prefix()
    {
        Guid id = Ids.New();
        string full = id.ToString("N");
        Ids.Matches(id, Ids.Short(id)).Should().BeTrue();
        Ids.Matches(id, full[..6]).Should().BeTrue();
        Ids.Matches(id, full[^5..]).Should().BeTrue();
        Ids.Matches(id, "zzzz").Should().BeFalse();
    }
}
