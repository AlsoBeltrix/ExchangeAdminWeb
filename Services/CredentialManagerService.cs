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

    public static bool StoreCredential(string target, string username, string password)
    {
        try
        {
            var vault = new PasswordVault();

            // Remove existing credential if present
            try
            {
                var existing = vault.FindAllByResource(target);
                foreach (var old in existing)
                    vault.Remove(old);
            }
            catch { }

            vault.Add(new PasswordCredential(target, username, password));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool HasCredential(string target)
    {
        try
        {
            var vault = new PasswordVault();
            var results = vault.FindAllByResource(target);
            return results.Any();
        }
        catch
        {
            return false;
        }
    }
}
