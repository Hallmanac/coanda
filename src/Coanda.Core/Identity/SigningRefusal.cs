namespace Coanda.Core.Identity;

/// <summary>Why a key may not sign for an origin.</summary>
public enum SigningRefusal
{
    /// <summary>The records hold no root record signed by the trusted root key.</summary>
    NoRoot,

    /// <summary>The key is revoked.</summary>
    Revoked,

    /// <summary>The key is a member's root key, and that member was removed.</summary>
    MemberRemoved,

    /// <summary>The node id is paired with more than one key, or the key with more than one node id.</summary>
    Conflicted,

    /// <summary>Every vouch for the key at that origin was signed by a key that may not sign.</summary>
    VouchersMayNotSign,

    /// <summary>The origin has no counting vouch for the key.</summary>
    NoCountingVouch,
}
