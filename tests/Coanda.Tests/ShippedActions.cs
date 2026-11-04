using Coanda.Core.Contours;
using Coanda.Core.Software;

namespace Coanda.Tests;

/// <summary>
/// The action names the daemon registers, for a test that validates or seeds contours without a
/// daemon. <c>FlowActionsTests</c> pins that the daemon's registration holds exactly these.
/// </summary>
internal static class ShippedActions
{
    public static readonly IReadOnlySet<string> Names =
        new HashSet<string>([.. CoreActions.All, .. SoftwareActions.All], StringComparer.Ordinal);
}
