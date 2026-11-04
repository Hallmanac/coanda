using Coanda.Core.Contours;
using Coanda.Core.Registry;

namespace Coanda.Daemon.Actions;

/// <summary>
/// One action step to perform: who performs it (the flow's principal), the flow and its parcel,
/// the contour and the step. <paramref name="Resumed"/> is true when the flow was resumed while this
/// visit was its current step, which is the event a wait step waits for.
/// </summary>
public sealed record ActionRequest(
    Principal Principal,
    string Project,
    Guid FlowId,
    Guid ParcelId,
    Contour Contour,
    ContourStep Step,
    bool Resumed);
