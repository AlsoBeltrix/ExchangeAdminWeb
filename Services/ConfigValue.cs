using System.Collections.Concurrent;
using System.Globalization;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// A number read from configuration that never throws (aud-a62), beside <see cref="ConfigBoolean"/>. The audit and trace
/// writer, the extended log, the mail service and the bulk job runner read theirs with GetValue&lt;int?&gt;,
/// GetValue&lt;long?&gt;, GetValue&lt;double?&gt; or int.Parse in their constructors, so one mistyped value - "50MB", "five" -
/// threw and the singleton could not be built: every page that needed it failed, and for the job runner, which is
/// resolved at start-up, the app did not start.
/// </summary>
/// <remarks>
/// A decimal value the binder read is read the same: the number styles and the invariant culture of the converters it
/// used. Blank or absent is the setting's documented default. Anything else that does not parse is that default too, and
/// is named in one warning, once per process for each setting and value.
/// </remarks>
internal static class ConfigValue
{
    // The settings and values already named in the log. A mistyped value is static: named once per process for each
    // setting and value (the aud-a43 shape, as ConfigBoolean keeps its own).
    private static readonly ConcurrentDictionary<(string Key, string Value), byte> Named = new();

    /// <summary>
    /// The whole number at <paramref name="key"/>, white space around it and a leading sign allowed. Absent or blank is
    /// <paramref name="whenBlank"/>, the setting's documented default. Anything else that is not a whole number an
    /// <see cref="int"/> holds is <paramref name="whenBlank"/> too, and is named once. Never throws.
    /// </summary>
    internal static int ReadInt(IConfiguration config, string key, int whenBlank, ILogger logger)
    {
        var text = config[key];
        if (string.IsNullOrWhiteSpace(text))
            return whenBlank;

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;

        NameOnce(key, text, "a whole number", Text(whenBlank), logger);
        return whenBlank;
    }

    /// <summary>
    /// As <see cref="ReadInt"/>, for a <see cref="long"/>. <paramref name="whenBlank"/> may be null, "not set": for a setting
    /// whose absence hands the choice to another one, as Audit:MaxFileBytes hands it to Audit:MaxFileMB.
    /// </summary>
    internal static long? ReadLong(IConfiguration config, string key, long? whenBlank, ILogger logger)
    {
        var text = config[key];
        if (string.IsNullOrWhiteSpace(text))
            return whenBlank;

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;

        NameOnce(key, text, "a whole number", whenBlank is { } reading ? Text(reading) : "not set", logger);
        return whenBlank;
    }

    /// <summary>
    /// The number at <paramref name="key"/>, a decimal point and an exponent allowed. It must be finite: "NaN" and
    /// "Infinity" were read by the binder, then threw out of TimeSpan.FromMinutes and FromDays in the same constructor.
    /// Absent or blank is <paramref name="whenBlank"/>; anything else that is not a finite number is too, and is named
    /// once. Never throws.
    /// </summary>
    internal static double ReadDouble(IConfiguration config, string key, double whenBlank, ILogger logger)
    {
        var text = config[key];
        if (string.IsNullOrWhiteSpace(text))
            return whenBlank;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
            return value;

        NameOnce(key, text, "a finite number", Text(whenBlank), logger);
        return whenBlank;
    }

    private static string Text(IFormattable value) => value.ToString(null, CultureInfo.InvariantCulture);

    private static void NameOnce(string key, string value, string kind, string reading, ILogger logger)
    {
        if (!Named.TryAdd((key, value), 0))
            return;

        try
        {
            logger.LogWarning(
                "The configuration value {Key} is '{Value}', which cannot be read as {Kind}; it is read as {Reading} until it is corrected",
                key, value, kind, reading);
        }
        catch (Exception)
        {
            // A provider that throws has nowhere to report its own failure (aud-a28u); the answer stands.
        }
    }
}
