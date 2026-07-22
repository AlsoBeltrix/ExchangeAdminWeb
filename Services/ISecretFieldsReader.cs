namespace ExchangeAdminWeb.Services;

/// <summary>
/// Narrow read seam over Delinea secret retrieval, so services that only need to pull a secret's
/// fields can depend on this instead of the whole <see cref="DelineaService"/> - and be unit-tested
/// without a live vault. Implemented by <see cref="DelineaService"/>; registered against the same
/// singleton instance in Program.cs.
/// </summary>
public interface ISecretFieldsReader
{
    /// <summary>
    /// Returns the named fields of the given Delinea secret, or null if the secret cannot be read
    /// (unconfigured bootstrap credentials, not found, or a vault error). Never throws for the
    /// unreadable case - callers fail closed on null.
    /// </summary>
    Task<Dictionary<string, string>?> GetSecretFieldsAsync(int secretId);
}
