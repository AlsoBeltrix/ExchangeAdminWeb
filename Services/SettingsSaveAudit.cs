using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Records a settings save that did not land, for the admin pages whose saves run in the page
/// (aud-a28h).
/// </summary>
/// <remarks>
/// The Module Configuration, Admin Settings and Exchange Online Config saves wrote a settings-change
/// record on success and nothing else, so a refused or failed attempt on section access or the
/// protected principals - the authorization stores - left no trace. They stay in the pages: each
/// spans its own store and service and records what only the page holds (the descriptor's secret
/// masking, the alias snapshot, the directory check and its activity), so the aud-a28f move into one
/// service does not fit them.
///
/// Both write <see cref="AuditService.LogSettingsChangeFailed"/>, the success record's action and
/// category, so a section's refusals and failures are filed beside its changes. A refusal carries
/// aud-a28's <c>refusedAt</c> and <c>mutationAttempted</c> false; a failure carries neither, because
/// the write may have been tried. <c>sections</c> is for the saves whose success writes one record per
/// section changed: their one record is filed under the save's scope and names what was pending.
///
/// Extensions rather than AuditService members, because they never throw: an audit sink that fails is
/// logged here, and must not turn a refusal into an exception or put a second record behind it. The
/// AuditService methods throw, and their callers catch.
/// </remarks>
public static class SettingsSaveAudit
{
    /// <summary>
    /// Records a settings save refused before anything was written: <c>refusedAt</c> names the step
    /// and <c>mutationAttempted</c> is false. Never throws.
    /// </summary>
    public static void LogSettingsSaveRefused(
        this AuditService audit,
        ILogger logger,
        string performedBy,
        string ip,
        string section,
        string refusedAt,
        string reason,
        IReadOnlyCollection<string>? sections = null)
    {
        var extra = new Dictionary<string, object?> { ["refusedAt"] = refusedAt, ["mutationAttempted"] = false };
        Write(audit, logger, performedBy, ip, section, reason, sections, extra);
    }

    /// <summary>
    /// Records a settings save that failed with an exception. Neither refusal field. Never throws.
    /// </summary>
    public static void LogSettingsSaveFailed(
        this AuditService audit,
        ILogger logger,
        string performedBy,
        string ip,
        string section,
        string reason,
        IReadOnlyCollection<string>? sections = null) =>
        Write(audit, logger, performedBy, ip, section, reason, sections, null);

    /// <summary>The <c>refusedAt</c> of a save refused because the settings store was busy (aud-a28v, aud-a40).</summary>
    public const string StoreBusy = "StoreBusy";

    /// <summary>Why a save that met a busy settings store was not made (aud-a40).</summary>
    public const string StoreBusyCause =
        "the settings store was busy - another save, on this instance or the other, held it for longer than this one could wait";

    /// <summary>
    /// What a save refused at a busy settings store tells the admin: that the store was busy, <paramref name="written"/>, and to
    /// try again (aud-a40).
    /// </summary>
    public static string StoreBusyMessage(string written = "Nothing was written.") =>
        $"Not saved: {StoreBusyCause}. {written} Try again in a moment; your changes are still on this page.";

    /// <summary>
    /// Records a settings save that ended in an exception and returns what the admin is told (aud-a40). A busy store
    /// (<see cref="ConfigStoreBusy.IsBusy"/>) is a refusal: logged as a warning with the exception, recorded with
    /// <c>refusedAt</c> <see cref="StoreBusy"/> and <c>mutationAttempted</c> false, and told with <paramref name="busy"/>, or
    /// <see cref="StoreBusyMessage"/>. Anything else is the save's failure, recorded and returned as <paramref name="failure"/>.
    /// Never throws.
    /// </summary>
    public static string LogSettingsSaveException(
        this AuditService audit,
        ILogger logger,
        string performedBy,
        string ip,
        string section,
        Exception exception,
        string failure,
        IReadOnlyCollection<string>? sections = null,
        string? busy = null)
    {
        if (ConfigStoreBusy.IsBusy(exception))
        {
            var message = busy ?? StoreBusyMessage();
            logger.LogWarning(exception, "Settings save not made: the settings store was busy ({Section})", section);
            LogSettingsSaveRefused(audit, logger, performedBy, ip, section, StoreBusy, message, sections);
            return message;
        }

        LogSettingsSaveFailed(audit, logger, performedBy, ip, section, failure, sections);
        return failure;
    }

    private static void Write(
        AuditService audit,
        ILogger logger,
        string performedBy,
        string ip,
        string section,
        string reason,
        IReadOnlyCollection<string>? sections,
        Dictionary<string, object?>? extra)
    {
        try
        {
            if (sections is { Count: > 0 })
            {
                extra ??= new Dictionary<string, object?>();
                extra["sections"] = sections.ToArray();
            }

            audit.LogSettingsChangeFailed(performedBy, ip, section, reason, extra);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit failed for a settings save that did not complete ({Section})", section);
        }
    }
}
