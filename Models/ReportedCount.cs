using System.Globalization;

namespace ExchangeAdminWeb.Models;

/// <summary>
/// aud-a23f. One count an Exchange cmdlet reported: the count, or - when what came back is not a whole
/// number this app can read - the name and text of what did come back, shown as the same marker a
/// <see cref="ReportedTime"/> it cannot read is shown as.
/// </summary>
/// <remarks>
/// Migration read each batch count with <c>Convert.ToInt32</c>, which throws on a value it cannot convert
/// (a wrapped number, text with a group separator, an object) and rounds a fraction. The throw lost the
/// whole batch from the list, with only a log line to say so. Which types the Exchange Online module hands
/// back is not known here (an assumption the finding record labels; no live call was made), so a count is
/// read only where it is a whole number beyond doubt, and anything else is kept and named - the
/// <see cref="ReportedTime"/> rule, whose unwrapping, naming and marker this shares rather than copies.
/// <para>
/// aud-a23g: the mailbox item counts read through it too, where <c>Convert.ToInt64</c> lost the whole mailbox
/// list the same way, so a count is a <see cref="long"/>; and a negative number is not a count of anything.
/// </para>
/// </remarks>
public sealed record ReportedCount
{
    private ReportedCount(long? count, string? unreadableType, string? unreadableText)
    {
        Count = count;
        UnreadableType = unreadableType;
        UnreadableText = unreadableText;
    }

    /// <summary>
    /// The count, when it could be read: a whole number from 0 up. aud-a23g: a <see cref="long"/>, because the
    /// mailbox item counts were Int64 reads, and an Int32 count would make a readable one above 2,147,483,647
    /// its marker.
    /// </summary>
    public long? Count { get; }

    /// <summary>The type name of what was reported instead, when it could not be read as a count.</summary>
    public string? UnreadableType { get; }

    /// <summary>The text of what was reported instead, cut as <see cref="ReportedTime.UnreadableText"/> is.</summary>
    public string? UnreadableText { get; }

    /// <summary>The count in the invariant culture, or the marker naming what came back and its text.</summary>
    public string Show() =>
        Count is { } count ? count.ToString(CultureInfo.InvariantCulture) : ReportedTime.Marker(UnreadableType, UnreadableText);

    /// <summary>
    /// <see cref="Show"/>: the page renders a count as it is (<c>@batch.TotalCount</c>) and the CSV calls
    /// <c>ToString()</c>, so one this app cannot read shows its marker in both - never a blank, and never
    /// the record's own dump of its fields.
    /// </summary>
    public override string ToString() => Show();

    /// <summary>A count already held as a number - a default, or a test's value. Not a second reader.</summary>
    public static ReportedCount Of(long count) => new(count, null, null);

    /// <summary>
    /// The reader for a reported count: null when nothing was reported. A <c>PSObject</c> is read as what it
    /// wraps; a whole number of any numeric type from 0 to <see cref="long"/>'s maximum as itself; text only in
    /// the invariant culture's integer form (digits, an optional sign, surrounding spaces). Anything else is not
    /// readable.
    /// </summary>
    /// <remarks>
    /// Reading anything else would be deciding something: a fraction would have to be rounded, a flag taken
    /// for one or nought, and <c>1,205</c> is over a thousand in one culture and a fraction in another, with
    /// nothing here to say which wrote it. Unreadable, it is named by the reader <see cref="ReportedTime"/>
    /// uses, so a page says the same of every reported value it cannot read.
    /// <para>
    /// aud-a23g: so is a negative number, whatever carries it. Nothing is counted below nought, and read as a
    /// count a FailedCount of -1 showed in the red failed cell as a fact and sorted before every 0.
    /// </para>
    /// </remarks>
    public static ReportedCount? Read(object? reported)
    {
        var value = ReportedTime.Unwrap(reported);
        if (value is null)
            return null;
        if (WholeNumber(value) is { } count)
            return new(count, null, null);

        var (typeName, shownText) = ReportedTime.Describe(reported!);
        return new(null, typeName, shownText);
    }

    /// <summary>
    /// An ordering by a reported count: the counts in the direction asked, then any this app cannot read,
    /// after every count in both directions and in list order among themselves.
    /// </summary>
    /// <remarks>
    /// An unreadable count is unknown, so it is neither the smallest nor the largest; at either end of a
    /// sorted list it would read as a fact. A comparer rather than two sort keys, so the page's three count
    /// sorts each stay one expression.
    /// </remarks>
    public static IComparer<ReportedCount?> Order(bool ascending) => ascending ? Ascending : Descending;

    private static readonly IComparer<ReportedCount?> Ascending = Comparer<ReportedCount?>.Create((x, y) => Compare(x, y, 1));
    private static readonly IComparer<ReportedCount?> Descending = Comparer<ReportedCount?>.Create((x, y) => Compare(x, y, -1));

    private static int Compare(ReportedCount? x, ReportedCount? y, int direction) => (x?.Count, y?.Count) switch
    {
        ({ } a, { } b) => direction * a.CompareTo(b),
        (null, null) => 0,
        (null, _) => 1,
        _ => -1,
    };

    // aud-a23g. A count: a whole number that is not negative. The one place the sign is checked, whatever type
    // carried the number.
    private static long? WholeNumber(object value) => Integer(value) is { } whole && whole >= 0 ? whole : null;

    // A whole number, whatever numeric type carries it; a fraction, a value out of range, NaN or an
    // infinity is not one. Text in the invariant culture's integer form only, so no culture is guessed.
    // aud-a23g: Int64's range, so the item counts lose nothing; 2^63 bounds a double, because long.MaxValue
    // has no exact double and every whole double below 2^63 converts exactly.
    private static long? Integer(object value)
    {
        switch (value)
        {
            case int or long or sbyte or byte or short or ushort or uint or ulong:
                var integral = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return integral is >= long.MinValue and <= long.MaxValue ? (long)integral : null;
            case decimal exact:
                return exact == decimal.Truncate(exact) && exact is >= long.MinValue and <= long.MaxValue ? (long)exact : null;
            case float or double:
                var real = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return double.IsFinite(real) && real == Math.Truncate(real) && real >= -9223372036854775808.0 && real < 9223372036854775808.0
                    ? (long)real
                    : null;
            case string text:
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            default:
                return null;
        }
    }
}
