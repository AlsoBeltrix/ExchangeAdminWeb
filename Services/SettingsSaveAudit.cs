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
