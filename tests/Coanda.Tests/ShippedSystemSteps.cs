using Coanda.Core.Flows;
using Coanda.Modules.Software;

namespace Coanda.Tests;

/// <summary>
/// The system step names the daemon registers, for a test that validates or seeds flows without a
/// daemon. <c>FlowSystemStepsTests</c> pins that the daemon's registration holds exactly these.
/// </summary>
internal static class ShippedSystemSteps
{
    public static readonly IReadOnlySet<string> Names =
        new HashSet<string>([.. CoreSystemSteps.All, .. SoftwareSteps.All], StringComparer.Ordinal);
}
