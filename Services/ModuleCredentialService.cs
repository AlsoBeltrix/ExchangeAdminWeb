namespace ExchangeAdminWeb.Services;

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

    public async Task<(string username, string password, string domain)?> GetCredentialsAsync(string moduleId, string purpose)
    {
        if (_moduleConfig.IsCorrupt)
        {
            _logger.LogError("Cannot retrieve credentials for {Module}: module-config.json is corrupt", moduleId);
            return null;
        }

        var secretIdValue = _moduleConfig.GetValue(moduleId, "DelineaSecretId");
        if (!int.TryParse(secretIdValue, out var secretId) || secretId <= 0)
        {
            _logger.LogError("Cannot retrieve credentials for {Module}: DelineaSecretId is not configured. Purpose: {Purpose}", moduleId, purpose);
            return null;
        }

        return await _delineaService.GetCredentialsBySecretIdAsync(secretId);
    }
}
