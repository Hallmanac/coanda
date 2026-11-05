namespace Coanda.Core.Identity;

/// <summary>Whether a root-set vouched pairing may be named a successor, and when it may not, why.</summary>
internal enum NominationStanding
{
    /// <summary>Naming it counts.</summary>
    Nameable,

    /// <summary>A counting successor revocation voided its key.</summary>
    Voided,

    /// <summary>Its key is a member's root key, and no member joins the ranked root set.</summary>
    Member,

    /// <summary>It already shares its key or its node with a key of the ranked root set.</summary>
    Ranked,
}
