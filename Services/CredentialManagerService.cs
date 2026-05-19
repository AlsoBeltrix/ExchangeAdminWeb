using System.Runtime.InteropServices;
using System.Text;

namespace ExchangeAdminWeb.Services;

public static class CredentialManagerService
{
    public static (string? username, string? password) ReadCredential(string target)
    {
        try
        {
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var credPtr))
                return (null, null);

            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                var username = cred.UserName;
                string? password = null;

                if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0)
                {
                    password = Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
                    password = password?.TrimEnd('\0');
                }

                return (username, password);
            }
            finally
            {
                CredFree(credPtr);
            }
        }
        catch
        {
            return (null, null);
        }
    }

    private const int CRED_TYPE_GENERIC = 1;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }
}
