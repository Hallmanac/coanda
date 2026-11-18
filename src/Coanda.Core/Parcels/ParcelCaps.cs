namespace Coanda.Core.Parcels;

/// <summary>
/// The longest title and field values <see cref="ParcelDecider"/> records, per kind of value rather
/// than per field, since a parcel type is user data and its field names vary. Lengths count UTF-16
/// characters, as <see cref="string.Length"/> does. The caps bind new writes on this node; a value
/// already in the record, or one a ledger import appends, is never measured against them.
/// </summary>
public static class ParcelCaps
{
    /// <summary>A title, after trimming. The longest objective in this project's planning records is 652 characters.</summary>
    public const int TitleLength = 1_000;

    /// <summary>A text value. GitHub allows a pull request body of 65,536 characters, and one may be copied into a text field.</summary>
    public const int TextLength = 65_536;

    /// <summary>One item of a list. The longest single criterion in this project's planning records is 3,475 characters.</summary>
    public const int ItemLength = 8_000;

    /// <summary>The items one list holds. The most criteria on one task in this project's planning records is 18.</summary>
    public const int ItemCount = 200;

    /// <summary>A reference's id, which is an identifier such as an issue key or a pull request number.</summary>
    public const int ReferenceIdLength = 1_000;

    /// <summary>A reference's URL, the length browsers and most servers reliably accept.</summary>
    public const int ReferenceUrlLength = 2_048;
}
