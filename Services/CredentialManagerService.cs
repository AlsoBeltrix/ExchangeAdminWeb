using Windows.Security.Credentials;

namespace ExchangeAdminWeb.Services;

public static class CredentialManagerService
{
    public static (string? username, string? password) ReadCredential(string target)
    {
        try
        {
            var vault = new PasswordVault();
            var results = vault.FindAllByResource(target);
            var cred = results.FirstOrDefault();

            if (cred is null)
                return (null, null);

            cred.RetrievePassword();
            return (cred.UserName, cred.Password);
        }
        catch
        {
            return (null, null);
        }
    }
}
