namespace Coanda.Tests.Core.Identity;

/// <summary>A node and its key, for identity fixtures. The keys are well formed but sign nothing: the trust rule reads signer keys, never signatures.</summary>
public sealed record Party(string Name, Guid Node, string Key)
{
    public override string ToString() => Name;
}

/// <summary>The parties identity fixtures are written with, each a distinct node id and key.</summary>
public static class Parties
{
    public static readonly Party Root = Make("root", 1);
    public static readonly Party A = Make("a", 2);
    public static readonly Party B = Make("b", 3);
    public static readonly Party C = Make("c", 4);
    public static readonly Party X = Make("x", 5);
    public static readonly Party S = Make("s", 6);
    public static readonly Party S1 = Make("s1", 7);
    public static readonly Party S2 = Make("s2", 8);
    public static readonly Party M = Make("m", 9);
    public static readonly Party N = Make("n", 10);

    public static readonly IReadOnlyList<Party> All = [Root, A, B, C, X, S, S1, S2, M, N];

    private static Party Make(string name, int n) =>
        new(name, new Guid($"00000000-0000-7000-8000-{n:D12}"), Convert.ToBase64String([.. Enumerable.Repeat((byte)n, 32)]));
}
