namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// The confidential-client settings for the SelfServiceGroups module's DEDICATED Entra app
/// registration (plan docs/SelfServiceGroupManagement-Plan.md section 6.8). This is the delegated
/// sign-in identity - distinct from the app-only Graph registration used elsewhere (credential
/// isolation, codex F3). Populated from a Delinea secret whose fields are Tenant ID, Application ID,
/// Client Secret - the same field names as the app-only Graph secret (verified against
/// M365GroupManagementService.GetGraphClientAsync), but a SEPARATE secret id.
/// </summary>
public sealed record DelegatedEntraSettings(string TenantId, string ClientId, string ClientSecret)
{
    /// <summary>The Delinea secret field name carrying the directory (tenant) id.</summary>
    public const string TenantIdField = "Tenant ID";

    /// <summary>The Delinea secret field name carrying the registration's application (client) id.</summary>
    public const string ClientIdField = "Application ID";

    /// <summary>The Delinea secret field name carrying the confidential-client secret.</summary>
    public const string ClientSecretField = "Client Secret";

    /// <summary>The module config key holding the Delinea secret id for the DELEGATED registration.</summary>
    public const string SecretConfigKey = "DelegatedGraphDelineaSecretId";

    /// <summary>The SelfServiceGroups module id (matches the ModuleCatalog descriptor).</summary>
    public const string ModuleId = "SelfServiceGroups";

    /// <summary>The OIDC authority for this tenant (v2.0 endpoint), derived from the tenant id.</summary>
    public string Authority => $"https://login.microsoftonline.com/{TenantId}/v2.0";
}
