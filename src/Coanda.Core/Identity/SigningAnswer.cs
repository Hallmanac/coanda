namespace Coanda.Core.Identity;

/// <summary>Whether a key may sign for an origin, and when it may not, why.</summary>
public sealed record SigningAnswer(bool MaySign, SigningRefusal? Refusal)
{
    public static readonly SigningAnswer Allowed = new(true, null);

    public static SigningAnswer Refused(SigningRefusal refusal) => new(false, refusal);
}
