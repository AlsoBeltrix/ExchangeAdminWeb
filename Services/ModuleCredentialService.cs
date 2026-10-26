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

    // aud-a2x. The lookup every caller has used, unchanged for them: the same credential or null, the same lines. kd-37: and the
    // caller's token, which goes on to the vault request; none, the default, is every caller's but Recipient Lookup's.
    public async Task<(string username, string password, string domain)?> GetCredentialsAsync(string moduleId, string purpose, CancellationToken cancellationToken = default) =>
        (await LookupCredentialsAsync(moduleId, purpose, cancellationToken)).Credentials;

    // aud-a2x. The same lookup, saying why it returned none. GetCredentialsAsync returned null alike for an unreadable
    // module config, for no secret ID, and for a configured ID the PAM provider answered with nothing, so a caller could
    // tell its operator only one of them - and Conference Rooms told every one to set the secret ID. The reason is the
    // lookup's own, from the reads it made, not a second read of the config that an edit in between could contradict.
    // Delinea's finer reasons - no bootstrap credential, an HTTP status, a secret missing a field, an exception - stay in
    // DelineaService's log lines, one for each null it returns: carrying them out would change a service every module uses.
    public async Task<ModuleCredentialLookup> LookupCredentialsAsync(string moduleId, string purpose, CancellationToken cancellationToken = default)
    {
        // kd-61. Before any answer of its own. The two configuration paths below return a failure the caller reports as a read
        // that could not be made, so a lookup whose operator had already cancelled finished, completed its activity as
        // succeeded and wrote a success audit row. The vault call throws on a cancelled token (kd-37); these return before it.
        cancellationToken.ThrowIfCancellationRequested();

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

        var credentials = await _delineaService.GetCredentialsBySecretIdAsync(secretId, cancellationToken); // kd-37: a cancelled token throws there
        return new ModuleCredentialLookup(
            credentials, credentials is null ? ModuleCredentialFailure.NotReturned : ModuleCredentialFailure.None);
    }
}
