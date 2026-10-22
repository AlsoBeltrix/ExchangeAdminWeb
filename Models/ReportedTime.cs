namespace ExchangeAdminWeb.Models;

/// <summary>
/// One time an Exchange cmdlet reported: the time, or - when what came back is not a form this app
/// can read as one - the name and text of what did come back. aud-a7h made it for the Out of Office
/// schedule; aud-a23d gave it the Migration batch and mailbox times and Recipient Lookup's two, which
/// is why it has a file of its own: <see cref="Read"/> stays the one reader for all of them.
/// </summary>
/// <remarks>
/// Each of those reads used to take its time <c>as DateTime?</c>, so any other type became null and
/// the page showed nothing, as if none were set: a Scheduled auto-reply with no window, a batch with
/// no start, a mailbox that had never synced. Which types the Exchange Online module hands back is
/// not known here (an assumption each finding record labels; no live call was made), so every form
/// that names a time unambiguously is read, and anything else is kept as its type name and its text
/// for the page to show - the onus is on the data, and nothing is guessed. aud-a7i: the text, because
/// showing what Exchange said word for word guesses nothing and lets the operator see it rather than
/// only what kind of thing it was.
/// </remarks>
public sealed record ReportedTime
{
    private ReportedTime(DateTime? time, string? unreadableType, string? unreadableText)
    {
        Time = time;
        UnreadableType = unreadableType;
        UnreadableText = unreadableText;
    }

    /// <summary>The time, when it could be read. Its Kind says whether it names an instant (aud-a7f).</summary>
    public DateTime? Time { get; }

    /// <summary>The type name of what was reported instead, when it could not be read as a time.</summary>
    public string? UnreadableType { get; }

    /// <summary>
    /// aud-a7i. The text of what was reported instead, when it could not be read as a time and has any:
    /// as it came, cut at <see cref="ShownTextLimit"/> characters.
    /// </summary>
    /// <remarks>
    /// Held raw, not HTML-encoded: a page renders it as text, which Blazor encodes on the way out, and the
    /// Migration CSV is not HTML at all, so encoding it here would put <c>&amp;lt;</c> in front of the
    /// operator. Cut because it comes from outside this app and lands in one line of a page or one cell;
    /// the cut is marked, so a shortened value is never shown as if it were the whole of it.
    /// </remarks>
    public string? UnreadableText { get; }

    /// <summary>
    /// The time as <paramref name="readable"/> shows one, or - when it could not be read - the marker
    /// naming what came back and its text.
    /// </summary>
    /// <remarks>
    /// The marker is spelled here once, so every page that shows a reported time says the same thing
    /// about one it cannot read, and none can show such a value as blank.
    /// </remarks>
    public string Show(Func<DateTime, string> readable) =>
        Time is { } time
            ? readable(time)
            : UnreadableText is null
                ? $"reported in a form this app cannot read ({UnreadableType})"
                : $"reported in a form this app cannot read ({UnreadableType}): {UnreadableText}";

    /// <summary>
    /// aud-a23d. A time already held as a <see cref="DateTime"/> - a default, or a test's value. Not a
    /// second reader: what a cmdlet reported goes through <see cref="Read"/>.
    /// </summary>
    public static ReportedTime Of(DateTime time) => new(time, null, null);

    /// <summary>
    /// aud-a23d. The first key of any sort by a reported time: 0 for a time (then ordered by
    /// <see cref="Time"/>), 1 for one this app cannot read, 2 for none reported - in both directions.
    /// </summary>
    /// <remarks>
    /// An unreadable time has no place among the times - read as none it would hide among the mailboxes
    /// that never synced, read as the earliest it would lead an ascending list - so it goes after every
    /// time, as unknown, and before none, because Exchange did report something for it.
    /// </remarks>
    public static int SortGroup(ReportedTime? reported) => reported is null ? 2 : reported.Time is null ? 1 : 0;

    /// <summary>
    /// The one reader for a reported time: null when nothing was reported. A <c>PSObject</c> is read as
    /// what it wraps; a <see cref="DateTime"/> as is; a <see cref="DateTimeOffset"/> as its instant, of
    /// Kind Utc; a string only in an ISO 8601 form, which with <c>Z</c> or an offset is that instant (Kind
    /// Utc) and with neither names no zone (Kind Unspecified). Anything else is not readable.
    /// </summary>
    /// <remarks>
    /// Text in any other form is not read because reading it would be a guess: <c>01/02/2026</c> is a
    /// different day in different cultures, and nothing here says which wrote it. ISO 8601 puts the year
    /// first in a fixed order, so the invariant culture reads it the same whoever wrote it. A wrapped
    /// value that cannot be read is named by a wrapper's first type name, which for a deserialized
    /// object says what it was, where its base object (a property bag) would not.
    /// <para>
    /// Which wrapper's (codex, folded into aud-a7i): wrappers are unwrapped one at a time, and the name
    /// kept is the innermost <c>Deserialized.*</c> one if any wrapper carries one, else the innermost
    /// wrapper's. A wrapper around a wrapper need not share its names - one whose names were read before
    /// the inner one was named keeps a generic type's - and the outermost is the one most likely to be
    /// generic, so keeping its name named the wrapper instead of what it wrapped. The text is the
    /// innermost wrapper's own, because a deserialized object carries the text its original had where
    /// its property bag has none.
    /// </para>
    /// </remarks>
    public static ReportedTime? Read(object? reported)
    {
        var value = reported;
        System.Management.Automation.PSObject? innermost = null;
        string? deserializedName = null;
        while (value is System.Management.Automation.PSObject wrapped)
        {
            innermost = wrapped;
            var name = FirstTypeName(wrapped);
            if (name?.StartsWith("Deserialized.", StringComparison.Ordinal) == true)
                deserializedName = name;
            value = wrapped.ImmediateBaseObject;
        }

        switch (value)
        {
            case null:
                return null;
            case DateTime time:
                return new(time, null, null);
            case DateTimeOffset instant:
                return new(instant.UtcDateTime, null, null);
            case string text when DateTime.TryParseExact(text, IsoForms, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                    out var parsed):
                return new(parsed, null, null);
            default:
                var typeName = deserializedName
                    ?? (innermost is null ? null : FirstTypeName(innermost))
                    ?? value.GetType().FullName
                    ?? value.GetType().Name;
                return new(null, typeName, TextOf(innermost ?? value));
        }
    }

    private static string? FirstTypeName(System.Management.Automation.PSObject wrapped) =>
        wrapped.TypeNames.Count > 0 ? wrapped.TypeNames[0] : null;

    private const int ShownTextLimit = 80;

    // aud-a7i. What was reported, as text, for the marker: none when it has no visible text, or when its
    // own ToString throws - a value from outside this app can carry any ToString - because showing the
    // text is a courtesy on top of naming the type, and must not turn a read that succeeded into one
    // that failed. The invariant culture, so a number reads the same on every server. A cut never
    // splits a surrogate pair, which would leave half a character on the page.
    private static string? TextOf(object value)
    {
        string? text;
        try
        {
            text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (text.Length <= ShownTextLimit)
            return text;
        var cut = char.IsHighSurrogate(text[ShownTextLimit - 1]) ? ShownTextLimit - 1 : ShownTextLimit;
        return text[..cut] + "...";
    }

    // Date, then T or a space, then the time to the minute or the second (fractions optional), then Z,
    // an offset or nothing - which AdjustToUniversal turns into Kind Utc, Utc, or Unspecified.
    private static readonly string[] IsoForms =
    [
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFK",
        "yyyy-MM-ddTHH:mmK",
        "yyyy-MM-dd HH:mmK",
    ];
}
