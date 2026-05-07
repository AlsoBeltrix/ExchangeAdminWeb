namespace ExchangeAdminWeb.Services;

public interface IIdentityResolver
{
    Task<string?> ResolveToObjectIdAsync(string identity);
}
