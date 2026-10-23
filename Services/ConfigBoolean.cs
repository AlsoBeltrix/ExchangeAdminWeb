using System.Collections.Concurrent;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// A Boolean read from configuration that never throws (aud-a58). The pages and services that read one used bool.Parse,
/// so a value that was neither true nor false - "yes", "1" - threw: the Home page failed to render for every user, and the
/// mail service and the ServiceNow client, each built once for the whole app, failed every page that needed them.
/// </summary>
internal static class ConfigBoolean
{
    // The settings and values already named in the log. A mistyped value is static, and the Home page reads its switch on
    // every render: named once per process for each setting and value (the aud-a43 shape), read the same way every time.
    private static readonly ConcurrentDictionary<(string Key, string Value), byte> Named = new();

    /// <summary>
    /// The Boolean at <paramref name="key"/>: true or false in any case, surrounding white space ignored. Absent or blank is
    /// <paramref name="whenBlank"/>, the setting's documented default. Any other value is <paramref name="whenNotBoolean"/>,
    /// the reading that does not widen what the setting guards, and is named in one warning, once per process for each
    /// setting and value. Never throws.
    /// </summary>
    internal static bool Read(IConfiguration config, string key, bool whenBlank, bool whenNotBoolean, ILogger logger)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value))
            return whenBlank;

        if (bool.TryParse(value, out var parsed))
            return parsed;

        if (Named.TryAdd((key, value), 0))
        {
            try
            {
                logger.LogWarning(
                    "The configuration value {Key} is '{Value}', which is neither true nor false; it is read as {Reading} until it is corrected",
                    key, value, whenNotBoolean ? "true" : "false");
            }
            catch (Exception)
            {
                // A provider that throws has nowhere to report its own failure (aud-a28u); the answer stands.
            }
        }

        return whenNotBoolean;
    }
}
