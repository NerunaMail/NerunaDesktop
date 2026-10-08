using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Neruna.Storage.Credentials;

/// <summary>
/// Windows Credential Manager ("Anmeldeinformationsverwaltung" → Windows-Anmeldeinformationen → "Neruna/…"),
/// generic credentials of the current user, protected by Windows with the user's logon.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCredentialBackend : ISecretBackend
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string Name => "Windows-Anmeldeinformationsverwaltung";

    public string? Get(string key)
    {
        if (!NativeMethods.CredReadW(Target(key), CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(pointer);
            return credential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    public void Set(string key, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var target = Marshal.StringToCoTaskMemUni(Target(key));
        var user = Marshal.StringToCoTaskMemUni(CredentialStoreSelector.Service);
        var data = Marshal.AllocCoTaskMem(Math.Max(1, blob.Length));
        try
        {
            Marshal.Copy(blob, 0, data, blob.Length);
            var credential = new NativeMethods.Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = blob.Length,
                CredentialBlob = data,
                Persist = CredPersistLocalMachine,
                UserName = user,
            };
            if (!NativeMethods.CredWriteW(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            // Do not leave the secret in freed memory.
            for (var i = 0; i < blob.Length; i++)
            {
                Marshal.WriteByte(data, i, 0);
            }

            Array.Clear(blob);
            Marshal.FreeCoTaskMem(data);
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(user);
        }
    }

    public void Delete(string key)
    {
        if (!NativeMethods.CredDeleteW(Target(key), CredTypeGeneric, 0) && Marshal.GetLastPInvokeError() is var error and not ErrorNotFound)
        {
            throw new Win32Exception(error);
        }
    }

    private static string Target(string key) => $"{CredentialStoreSelector.Service}/{key}";

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Credential
        {
            public int Flags;
            public int Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public uint LastWrittenLow;
            public uint LastWrittenHigh;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWriteW(ref Credential credential, int flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDeleteW(string target, int type, int flags);

        [DllImport("advapi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void CredFree(IntPtr buffer);
    }
}
