namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// Resolves the SelfServiceGroups module's dedicated delegated-Entra registration settings from
/// Delinea, fail-closed. Mirrors M365GroupManagementService.GetGraphClientAsync (verified section
/// 6.0 of the plan): read the module config key for the secret id, pull the secret's fields, and
/// require all three of Tenant ID / Application ID / Client Secret. Any missing piece yields a
/// null result (never a partial/permissive settings object) so callers cannot proceed with an
/// incomplete confidential-client credential.
///
/// virtual members are test seams (same pattern as ProtectedPrincipalService / EmailService) so the
/// auth wiring can be exercised without a live Delinea/Entra backend.
/// </summary>
public class DelegatedEntraSettingsProvider
{
    private readonly ModuleConfigService _moduleConfig;
    private readonly ISecretFieldsReader _delinea;
    private readonly ILogger<DelegatedEntraSettingsProvider> _logger;

    public DelegatedEntraSettingsProvider(
        ModuleConfigService moduleConfig,
        ISecretFieldsReader delinea,
        ILogger<DelegatedEntraSettingsProvider> logger)
    {
        _moduleConfig = moduleConfig;
        _delinea = delinea;
        _logger = logger;
    }

    /// <summary>
    /// True only if a positive Delinea secret id is configured for the delegated registration.
    /// This is a presence probe, not a validity guarantee - <see cref="GetSettingsAsync"/> still
    /// fails closed if the secret cannot be read or is incomplete.
    /// </summary>
    public virtual bool IsConfigured
    {
        get
        {
            var raw = _moduleConfig.GetValue(DelegatedEntraSettings.ModuleId, DelegatedEntraSettings.SecretConfigKey);
            return int.TryParse(raw, out var id) && id > 0;
        }
    }

    /// <summary>
    /// Returns the delegated registration settings, or null if the module is unconfigured, the
    /// secret cannot be retrieved, or any of the three required fields is blank. Never returns a
    /// partially-populated result - the delegated confidential client is all-or-nothing.
    /// </summary>
    public virtual async Task<DelegatedEntraSettings?> GetSettingsAsync()
    {
        var raw = _moduleConfig.GetValue(DelegatedEntraSettings.ModuleId, DelegatedEntraSettings.SecretConfigKey);
        if (!int.TryParse(raw, out var secretId) || secretId <= 0)
        {
            _logger.LogWarning("SelfServiceGroups delegated Entra secret id is not configured; delegated sign-in unavailable.");
            return null;
        }

        var fields = await _delinea.GetSecretFieldsAsync(secretId);
        if (fields == null)
        {
            _logger.LogWarning("Cannot retrieve SelfServiceGroups delegated Entra secret {SecretId} from Delinea.", secretId);
            return null;
        }

        var tenantId = fields.GetValueOrDefault(DelegatedEntraSettings.TenantIdField) ?? "";
        var clientId = fields.GetValueOrDefault(DelegatedEntraSettings.ClientIdField) ?? "";
        var clientSecret = fields.GetValueOrDefault(DelegatedEntraSettings.ClientSecretField) ?? "";

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            _logger.LogWarning("SelfServiceGroups delegated Entra secret {SecretId} is incomplete (missing tenant/client/secret); failing closed.", secretId);
            return null;
        }

        return new DelegatedEntraSettings(tenantId, clientId, clientSecret);
    }
}
