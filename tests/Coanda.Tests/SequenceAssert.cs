namespace Coanda.Tests;

/// <summary>
/// Order checks over a sequence that xUnit's Assert has no single call for. Each one fails with the
/// sequence it searched, so a failure shows what the code actually did.
/// </summary>
public static class SequenceAssert
{
    /// <summary>
    /// Asserts that <paramref name="expected"/> appears in <paramref name="actual"/> in the same order,
    /// with any other items allowed before, between, and after them.
    /// </summary>
    public static void ContainsInOrder<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        List<T> items = [.. actual];
        int from = 0;
        foreach (T item in expected)
        {
            int at = items.FindIndex(from, candidate => EqualityComparer<T>.Default.Equals(candidate, item));
            Assert.True(at >= 0, $"Expected {item} at or after position {from} in [{string.Join(", ", items)}].");
            from = at + 1;
        }
    }

    /// <summary>
    /// Asserts that <paramref name="expected"/> appears in <paramref name="actual"/> as one unbroken run,
    /// in the same order, anywhere in the sequence.
    /// </summary>
    public static void ContainsConsecutively<T>(IReadOnlyList<T> expected, IEnumerable<T> actual)
    {
        List<T> items = [.. actual];
        bool found = Enumerable.Range(0, Math.Max(0, items.Count - expected.Count + 1))
            .Any(start => items.Skip(start).Take(expected.Count).SequenceEqual(expected));
        Assert.True(found, $"Expected [{string.Join(", ", expected)}] as one run in [{string.Join(", ", items)}].");
    }
}
