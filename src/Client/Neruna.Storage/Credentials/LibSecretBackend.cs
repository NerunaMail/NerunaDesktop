using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Neruna.Storage.Credentials;

/// <summary>
/// Linux Secret Service through libsecret (GNOME Keyring, KWallet, KeePassXC …): one item per secret with the schema
/// "org.neruna.Secret" and the attribute "id", labelled "Neruna" in the default keyring.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LibSecretBackend : ISecretBackend
{
    private const string LibSecret = "libsecret-1.so.0";
    private const string GLib = "libglib-2.0.so.0";

    // SecretSchema: name, flags, 32 × {name, type}, reserved int, 7 reserved pointers (592 bytes on 64-bit).
    private const int SchemaSize = 592;
    private const int AttributesOffset = 16;

    private static readonly IntPtr SchemaName = Marshal.StringToCoTaskMemUTF8("org.neruna.Secret");
    private static readonly IntPtr IdAttribute = Marshal.StringToCoTaskMemUTF8("id");
    private static readonly IntPtr Label = Marshal.StringToCoTaskMemUTF8(CredentialStoreSelector.Service);
    private static readonly Lazy<IntPtr> Schema = new(CreateSchema);

    private readonly IntPtr _strHash;
    private readonly IntPtr _strEqual;

    private LibSecretBackend(IntPtr glib)
    {
        _strHash = NativeLibrary.GetExport(glib, "g_str_hash");
        _strEqual = NativeLibrary.GetExport(glib, "g_str_equal");
    }

    public string Name => "Linux Secret Service (Schlüsselbund)";

    /// <summary>Null if libsecret is not installed.</summary>
    public static LibSecretBackend? TryCreate(ILogger logger)
    {
        if (!NativeLibrary.TryLoad(LibSecret, out _) || !NativeLibrary.TryLoad(GLib, out var glib))
        {
            logger.LogInformation("libsecret not installed");
            return null;
        }

        return new LibSecretBackend(glib);
    }

    public string? Get(string key) => WithAttributes(key, attributes =>
    {
        var result = NativeMethods.secret_password_lookupv_sync(Schema.Value, attributes, IntPtr.Zero, out var error);
        ThrowIfError(error);
        if (result == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(result);
        }
        finally
        {
            NativeMethods.secret_password_free(result);
        }
    });

    public void Set(string key, string secret) => WithAttributes(key, attributes =>
    {
        var password = Marshal.StringToCoTaskMemUTF8(secret);
        try
        {
            NativeMethods.secret_password_storev_sync(Schema.Value, attributes, IntPtr.Zero, Label, password, IntPtr.Zero, out var error);
            ThrowIfError(error);
            return (string?)null;
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUTF8(password);
        }
    });

    public void Delete(string key) => WithAttributes(key, attributes =>
    {
        NativeMethods.secret_password_clearv_sync(Schema.Value, attributes, IntPtr.Zero, out var error);
        ThrowIfError(error);
        return (string?)null;
    });

    private string? WithAttributes(string key, Func<IntPtr, string?> action)
    {
        var table = NativeMethods.g_hash_table_new(_strHash, _strEqual);
        var value = Marshal.StringToCoTaskMemUTF8(key);
        try
        {
            NativeMethods.g_hash_table_insert(table, IdAttribute, value);
            return action(table);
        }
        finally
        {
            NativeMethods.g_hash_table_unref(table);
            Marshal.FreeCoTaskMem(value);
        }
    }

    private static IntPtr CreateSchema()
    {
        var schema = Marshal.AllocHGlobal(SchemaSize);
        for (var i = 0; i < SchemaSize; i++)
        {
            Marshal.WriteByte(schema, i, 0);
        }

        Marshal.WriteIntPtr(schema, 0, SchemaName);
        Marshal.WriteIntPtr(schema, AttributesOffset, IdAttribute); // type 0 = string; the terminating entry stays zero
        return schema;
    }

    private static void ThrowIfError(IntPtr error)
    {
        if (error == IntPtr.Zero)
        {
            return;
        }

        // GError: domain (uint32), code (int32), message (char*).
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "unbekannter Fehler";
        NativeMethods.g_error_free(error);
        throw new InvalidOperationException("Secret Service: " + message);
    }

    private static class NativeMethods
    {
        [DllImport(LibSecret)]
        internal static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

        [DllImport(LibSecret)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password, IntPtr cancellable, out IntPtr error);

        [DllImport(LibSecret)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

        [DllImport(LibSecret)]
        internal static extern void secret_password_free(IntPtr password);

        [DllImport(GLib)]
        internal static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr equalFunc);

        [DllImport(GLib)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

        [DllImport(GLib)]
        internal static extern void g_hash_table_unref(IntPtr table);

        [DllImport(GLib)]
        internal static extern void g_error_free(IntPtr error);
    }
}
