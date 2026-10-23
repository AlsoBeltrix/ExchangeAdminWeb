namespace ExchangeAdminWeb.Services;

/// <summary>Why <see cref="ModuleCredentialService.LookupCredentialsAsync"/> returned no credential (aud-a2x).</summary>
public enum ModuleCredentialFailure
{
    /// <summary>A credential was returned.</summary>
    None,

    /// <summary>The module's configuration could not be read, so neither could its secret ID.</summary>
    ConfigUnreadable,

    /// <summary>No valid DelineaSecretId is configured for the module.</summary>
    SecretIdNotSet,

    /// <summary>A secret ID is configured and the PAM provider returned no credential for it.</summary>
    NotReturned,
}

/// <summary>A module credential lookup's answer: the credential, or why there is none (aud-a2x).</summary>
public readonly record struct ModuleCredentialLookup(
    (string username, string password, string domain)? Credentials,
    ModuleCredentialFailure Failure);

public class ModuleCredentialService
{
    private readonly ModuleConfigService _moduleConfig;
    private readonly DelineaService _delineaService;
    private readonly ILogger<ModuleCredentialService> _logger;

    public ModuleCredentialService(
        ModuleConfigService moduleConfig,
        DelineaService delineaService,
        ILogger<ModuleCredentialService> logger)
    {
        _moduleConfig = moduleConfig;
        _delineaService = delineaService;
        _logger = logger;
    }

    public async Task<(string username, string password, string domain)?> GetCredentialsAsync(string moduleId, string purpose) =>
        (await LookupCredentialsAsync(moduleId, purpose)).Credentials;

    public async Task<ModuleCredentialLookup> LookupCredentialsAsync(string moduleId, string purpose)
    {
        if (_moduleConfig.IsModuleCorrupt(moduleId))
        {
            _logger.LogError("Cannot retrieve credentials for {Module}: module config is corrupt", moduleId);
            return new ModuleCredentialLookup(null, ModuleCredentialFailure.ConfigUnreadable);
        }

        var secretIdValue = _moduleConfig.GetValue(moduleId, "DelineaSecretId");
        if (!int.TryParse(secretIdValue, out var secretId) || secretId <= 0)
        {
            _logger.LogError("Cannot retrieve credentials for {Module}: DelineaSecretId is not configured. Purpose: {Purpose}", moduleId, purpose);
            return new ModuleCredentialLookup(null, ModuleCredentialFailure.SecretIdNotSet);
        }

        var credentials = await _delineaService.GetCredentialsBySecretIdAsync(secretId);
        return new ModuleCredentialLookup(
            credentials, credentials is null ? ModuleCredentialFailure.NotReturned : ModuleCredentialFailure.None);
    }
}
