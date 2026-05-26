using System.Runtime.InteropServices;
using System.Text;

namespace ExchangeAdminWeb.Services;

public static class CredentialManagerService
{
    public static (string? username, string? password) ReadCredential(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var credPtr))
            return (null, null);

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            var username = cred.UserName;
            string? password = null;

            if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0)
                password = Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);

            return (username, password);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public static bool StoreCredential(string target, string username, string password)
    {
        var passwordBytes = Encoding.Unicode.GetBytes(password);

        var cred = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = target,
            UserName = username,
            CredentialBlob = Marshal.AllocHGlobal(passwordBytes.Length),
            CredentialBlobSize = (uint)passwordBytes.Length,
            Persist = CRED_PERSIST_LOCAL_MACHINE
        };

        try
        {
            Marshal.Copy(passwordBytes, 0, cred.CredentialBlob, passwordBytes.Length);
            return CredWrite(ref cred, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(cred.CredentialBlob);
        }
    }

    public static bool HasCredential(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var credPtr))
            return false;

        CredFree(credPtr);
        return true;
    }

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }
}
