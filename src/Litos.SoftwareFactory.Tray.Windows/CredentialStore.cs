using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Litos.SoftwareFactory.Tray.Windows;

/// <summary>
/// The token in Windows Credential Manager (§26.3: never in a file): a generic credential per
/// factory address, visible to this Windows user only, under "Litos Factory Tray: &lt;address&gt;"
/// in Control Panel → Credential Manager → Windows Credentials.
/// </summary>
internal sealed class CredentialStore(string prefix = CredentialStore.DefaultPrefix) : ITokenStore
{
    public const string DefaultPrefix = "Litos Factory Tray: ";

    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;

    public string TargetOf(Uri address) => prefix + address.GetLeftPart(UriPartial.Path);

    public string? Read(Uri address)
    {
        if (!CredRead(TargetOf(address), GenericCredential, 0, out var found))
        {
            var error = Marshal.GetLastWin32Error();
            return error == NotFound ? null : throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(found);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(found);
        }
    }

    public void Write(Uri address, string token)
    {
        var bytes = Encoding.Unicode.GetBytes(token);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = TargetOf(address),
                Comment = "The access token the Litos Factory tray app signs in with.",
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = "access-token",
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            // The blob is not a terminated string, so it is cleared by its length before it is freed.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    public void Delete(Uri address)
    {
        if (!CredDelete(TargetOf(address), GenericCredential, 0) && Marshal.GetLastWin32Error() is var error and not NotFound)
            throw new Win32Exception(error);
    }

    /// <summary>Deletes the token of every factory address (a purged uninstall). Returns how many.</summary>
    public int DeleteAll()
    {
        if (!CredEnumerate(prefix + "*", 0, out var count, out var list))
        {
            var error = Marshal.GetLastWin32Error();
            return error == NotFound ? 0 : throw new Win32Exception(error);
        }

        var targets = new List<string>();
        try
        {
            for (var i = 0; i < count; i++)
                targets.Add(Marshal.PtrToStructure<Credential>(Marshal.ReadIntPtr(list, i * IntPtr.Size)).TargetName);
        }
        finally
        {
            CredFree(list);
        }

        foreach (var target in targets)
        {
            if (!CredDelete(target, GenericCredential, 0) && Marshal.GetLastWin32Error() is var error and not NotFound)
                throw new Win32Exception(error);
        }

        return targets.Count;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerate(string filter, uint flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
