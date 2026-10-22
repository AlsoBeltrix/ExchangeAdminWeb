namespace ExchangeAdminWeb.Services;

/// <summary>
/// Records a settings save that did not land, for the admin pages whose saves run in the page
/// (aud-a28h).
/// </summary>
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
