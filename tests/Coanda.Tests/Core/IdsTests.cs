using Coanda.Core;

namespace Coanda.Tests.Core;

public sealed class IdsTests
{
    [Fact]
    public void Short_ids_of_records_created_in_the_same_moment_differ()
    {
        List<string> shorts = Enumerable.Range(0, 50).Select(_ => Ids.Short(Ids.New())).ToList();
        // the short form is the random tail, not the timestamp head
        Assert.Distinct(shorts);
        Assert.All(shorts, s => Assert.Equal(8, s.Length));
    }

    [Fact]
    public void A_fragment_matches_by_suffix_or_prefix()
    {
        Guid id = Ids.New();
        string full = id.ToString("N");
        Assert.True(Ids.Matches(id, Ids.Short(id)));
        Assert.True(Ids.Matches(id, full[..6]));
        Assert.True(Ids.Matches(id, full[^5..]));
        Assert.False(Ids.Matches(id, "zzzz"));
    }
}
