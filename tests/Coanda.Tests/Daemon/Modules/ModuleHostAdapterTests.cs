using System.Reflection;
using Coanda.Daemon.Modules;
using Coanda.Sdk.Hosting;
using Coanda.Tests.Sdk.Contract;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// The host adapter is allowed no call the module API's schemas do not define: each of its public
/// methods is named after the call it carries, and every one of those calls is in the schemas' index.
/// </summary>
public sealed class ModuleHostAdapterTests
{
    [Fact]
    public void Every_public_method_of_the_adapter_carries_a_call_the_index_of_calls_lists()
    {
        IReadOnlySet<string> listed = ModuleSchemas.Calls.Select(c => c.Call).ToHashSet(StringComparer.Ordinal);

        IReadOnlyList<string> carried = CallsOf(typeof(ModuleHostAdapter));

        Assert.NotEmpty(carried);
        Assert.All(carried, call => Assert.True(listed.Contains(call), $"The host adapter offers '{call}', which the module API's index of calls does not list."));
        Assert.Empty(typeof(ModuleHostAdapter).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        Assert.Empty(typeof(ModuleHostAdapter).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
    }

    [Fact]
    public void The_adapter_offers_health_describe_dispatch_status_stop_and_the_feed()
    {
        Assert.Equal(["describe", "dispatch", "feed", "health", "status", "stop"], CallsOf(typeof(ModuleHostAdapter)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_hosted_module_is_called_through_exactly_the_calls_the_adapter_offers()
    {
        IReadOnlySet<string> listed = ModuleSchemas.Calls.Select(c => c.Call).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(CallsOf(typeof(ModuleHostAdapter)).Order(StringComparer.Ordinal), CallsOf(typeof(IModule)).Order(StringComparer.Ordinal));
        Assert.All(CallsOf(typeof(IModule)), call => Assert.Contains(call, listed));
        Assert.Empty(typeof(IModule).GetInterfaces());
        Assert.Empty(typeof(IModule).GetProperties());
    }

    [Fact]
    public void A_method_named_after_no_call_in_the_index_is_caught()
    {
        IReadOnlySet<string> listed = ModuleSchemas.Calls.Select(c => c.Call).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("remove", CallsOf(typeof(WiderAdapter)));
        Assert.DoesNotContain("remove", listed);
    }

    /// <summary>
    /// The call each public method of <paramref name="adapter"/> carries: its name without the
    /// <c>Async</c> suffix, from camel case into the index's kebab case (<c>DispatchAsync</c> carries
    /// <c>dispatch</c>). Methods every object has are not the adapter's.
    /// </summary>
    internal static IReadOnlyList<string> CallsOf(Type adapter) =>
    [
        .. adapter.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => Kebab(m.Name.EndsWith("Async", StringComparison.Ordinal) ? m.Name[..^"Async".Length] : m.Name)),
    ];

    private static string Kebab(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c)
            ? (i == 0 ? string.Empty : "-") + char.ToLowerInvariant(c)
            : c.ToString()));

    /// <summary>An adapter that offers a call the schemas do not define, which the rule must catch.</summary>
    private sealed class WiderAdapter
    {
        public static Task RemoveAsync(CancellationToken cancellationToken) => Task.Delay(0, cancellationToken);
    }
}
