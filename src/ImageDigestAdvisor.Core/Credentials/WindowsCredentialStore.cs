using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ImageDigestAdvisor.Core.Credentials;

/// <summary>
/// Stores registry accounts in Windows Credential Manager (generic credentials named
/// <c>ImageDigestAdvisor:&lt;registry&gt;</c>). Works for packaged (MSIX/Store) and unpackaged builds.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : IAppCredentialStore
{
    private const string Prefix = "ImageDigestAdvisor:";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public Task<RegistryCredential?> GetAsync(string registry, CancellationToken cancellationToken = default)
    {
        var target = Prefix + RegistryNames.Normalize(registry);
        if (!CredRead(target, CredTypeGeneric, 0, out var ptr))
        {
            return Task.FromResult<RegistryCredential?>(null);
        }

        try
        {
            var cred = Marshal.PtrToStructure<NativeCredential>(ptr);
            var secret = cred.CredentialBlobSize > 0
                ? Encoding.Unicode.GetString(ToBytes(cred.CredentialBlob, (int)cred.CredentialBlobSize))
                : string.Empty;
            var user = Marshal.PtrToStringUni(cred.UserName) ?? string.Empty;
            return Task.FromResult<RegistryCredential?>(new RegistryCredential(RegistryNames.Normalize(registry), user, secret, CredentialSource.App));
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public Task<IReadOnlyList<RegistryAccount>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<RegistryAccount>();
        if (!CredEnumerate(Prefix + "*", 0, out var count, out var list))
        {
            return Task.FromResult<IReadOnlyList<RegistryAccount>>(result);
        }

        try
        {
            for (var i = 0; i < count; i++)
            {
                var itemPtr = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<NativeCredential>(itemPtr);
                var target = Marshal.PtrToStringUni(cred.TargetName) ?? string.Empty;
                if (!target.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                result.Add(new RegistryAccount(target[Prefix.Length..], Marshal.PtrToStringUni(cred.UserName) ?? string.Empty, CredentialSource.App));
            }
        }
        finally
        {
            CredFree(list);
        }

        return Task.FromResult<IReadOnlyList<RegistryAccount>>(result.OrderBy(a => a.Registry, StringComparer.Ordinal).ToList());
    }

    public void Save(string registry, string username, string secret)
    {
        var target = Prefix + RegistryNames.Normalize(registry);
        var secretBytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(secretBytes.Length);
        var targetPtr = Marshal.StringToHGlobalUni(target);
        var userPtr = Marshal.StringToHGlobalUni(username);
        try
        {
            Marshal.Copy(secretBytes, 0, blob, secretBytes.Length);
            var cred = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                UserName = userPtr,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)secretBytes.Length,
                Persist = CredPersistLocalMachine,
            };
            if (!CredWrite(ref cred, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the account in Windows Credential Manager.");
            }
        }
        finally
        {
            // Overwrite the secret copy before freeing it.
            Marshal.Copy(new byte[secretBytes.Length], 0, blob, secretBytes.Length);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
        }
    }

    public void Remove(string registry)
    {
        var target = Prefix + RegistryNames.Normalize(registry);
        if (!CredDelete(target, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound) throw new Win32Exception(error, "Could not remove the account from Windows Credential Manager.");
        }
    }

    private static byte[] ToBytes(IntPtr ptr, int size)
    {
        var bytes = new byte[size];
        Marshal.Copy(ptr, bytes, 0, size);
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
