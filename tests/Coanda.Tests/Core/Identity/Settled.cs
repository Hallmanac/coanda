using System.Text;
using Coanda.Core.Identity;

namespace Coanda.Tests.Core.Identity;

/// <summary>
/// Evaluates a fixture after checking that every ordering of its records gives the same answer for
/// every key and origin the fixture names, the same ranked root set, and the same roles.
/// </summary>
public static class Settled
{
    public const int MaxRecords = 8;

    public static IdentityVerdict Of(Ledger ledger, Party? trusted = null)
    {
        string root = (trusted ?? Parties.Root).Key;
        IReadOnlyList<IdentityRecord> records = ledger.Records;
        Assert.True(records.Count <= MaxRecords, $"A fixture holds at most {MaxRecords} records; this one holds {records.Count}.");

        IdentityVerdict verdict = IdentityTrust.Evaluate(root, records);
        string expected = Answers(verdict, records);
        int orderings = 0;
        foreach (IdentityRecord[] ordering in Orderings([.. records]))
        {
            orderings++;
            string answers = Answers(IdentityTrust.Evaluate(root, ordering), records);
            if (answers != expected)
            {
                Assert.Fail($"Ordering {orderings} answered differently.\nExpected:\n{expected}\nActual:\n{answers}");
            }
        }

        Assert.Equal(Factorial(records.Count), orderings);
        return verdict;
    }

    /// <summary>Every answer the verdict gives about the keys and node ids the records name, as text.</summary>
    private static string Answers(IdentityVerdict verdict, IReadOnlyList<IdentityRecord> records)
    {
        HashSet<string> keys = [.. Parties.All.Select(p => p.Key), .. records.Select(r => r.Signer)];
        HashSet<Guid> nodes = [.. Parties.All.Select(p => p.Node), .. records.Select(r => r.Origin)];
        var text = new StringBuilder();
        text.AppendLine($"root set: {string.Join(", ", verdict.RootSet)}");
        foreach (string key in keys.Order(StringComparer.Ordinal))
        {
            text.AppendLine($"role {key}: {verdict.RoleOf(key)}");
            foreach (Guid node in nodes.Order())
            {
                text.AppendLine($"{key} for {node}: {verdict.MaySign(key, node)}");
            }
        }

        return text.ToString();
    }

    /// <summary>Every permutation of <paramref name="items"/>, by Heap's algorithm.</summary>
    private static IEnumerable<IdentityRecord[]> Orderings(IdentityRecord[] items)
    {
        int[] c = new int[items.Length];
        yield return [.. items];
        int i = 0;
        while (i < items.Length)
        {
            if (c[i] < i)
            {
                int j = i % 2 == 0 ? 0 : c[i];
                (items[j], items[i]) = (items[i], items[j]);
                yield return [.. items];
                c[i]++;
                i = 0;
            }
            else
            {
                c[i] = 0;
                i++;
            }
        }
    }

    private static int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);
}
