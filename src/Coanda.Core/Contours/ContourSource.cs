namespace Coanda.Core.Contours;

/// <summary>
/// One source a judgment step's context carries, naming exactly one kind. The only kind is a
/// <see cref="Step"/> of the same contour, whose latest recorded answer or result on the same flow the
/// context carries, provided it was recorded after the declaring step's previous visit; one recorded
/// before that visit is one the step has already acted on, and is not carried again. A visit a resume
/// performs again in place of one that answered without routing on does not count as a previous one.
/// Each kind's property is nullable, so content naming none of them, or a null one, is read and then
/// refused by validation with the source's position rather than by the reader.
/// </summary>
public sealed record ContourSource
{
    /// <summary>The step of the same contour this source reads, or null when the source names none.</summary>
    public string? Step { get; init; }
}
