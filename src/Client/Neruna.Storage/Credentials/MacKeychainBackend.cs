using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Neruna.Storage.Credentials;

/// <summary>
/// macOS login keychain ("Schlüsselbundverwaltung" → Passwörter → "Neruna"), generic passwords with service "Neruna"
/// and the connection ID as account.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacKeychainBackend : ISecretBackend
{
    private const int ErrSecItemNotFound = -25300;
    private static readonly byte[] Service = Encoding.UTF8.GetBytes(CredentialStoreSelector.Service);

    public string Name => "macOS-Schlüsselbund";

    public string? Get(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var status = NativeMethods.SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, out var length, out var data, out var item);
        if (status == ErrSecItemNotFound)
        {
            return null;
        }

        Check(status);
        try
        {
            return length == 0 ? string.Empty : Marshal.PtrToStringUTF8(data, (int)length);
        }
        finally
        {
            Release(data, item);
        }
    }

    public void Set(string key, string secret)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var password = Encoding.UTF8.GetBytes(secret);
        try
        {
            var status = NativeMethods.SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, out _, out var data, out var item);
            if (status == ErrSecItemNotFound)
            {
                Check(NativeMethods.SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, (uint)password.Length, password, IntPtr.Zero));
                return;
            }

            Check(status);
            try
            {
                Check(NativeMethods.SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password));
            }
            finally
            {
                Release(data, item);
            }
        }
        finally
        {
            Array.Clear(password);
        }
    }

    public void Delete(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var status = NativeMethods.SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, out _, out var data, out var item);
        if (status == ErrSecItemNotFound)
        {
            return;
        }

        Check(status);
        try
        {
            Check(NativeMethods.SecKeychainItemDelete(item));
        }
        finally
        {
            Release(data, item);
        }
    }

    // The returned status of freeing is irrelevant: there is nothing left to do if it fails.
    private static void Release(IntPtr data, IntPtr item)
    {
        _ = NativeMethods.SecKeychainItemFreeContent(IntPtr.Zero, data);
        NativeMethods.CFRelease(item);
    }

    private static void Check(int status)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"Schlüsselbund-Fehler {status}");
        }
    }

    private static class NativeMethods
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(Security)]
        internal static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

        [DllImport(Security)]
        internal static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, IntPtr itemRef);

        [DllImport(Security)]
        internal static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

        [DllImport(Security)]
        internal static extern int SecKeychainItemDelete(IntPtr itemRef);

        [DllImport(Security)]
        internal static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport(CoreFoundation)]
        internal static extern void CFRelease(IntPtr cf);
    }
}
