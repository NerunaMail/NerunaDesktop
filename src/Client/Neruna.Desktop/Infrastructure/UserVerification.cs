using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Neruna.Desktop.Infrastructure;

/// <summary>The answer of <see cref="UserVerification"/>.</summary>
internal enum UserCheck
{
    /// <summary>The person at the computer knows its login.</summary>
    Confirmed,

    /// <summary>The dialog was closed.</summary>
    Cancelled,

    /// <summary>Wrong password.</summary>
    Wrong,

    /// <summary>Signed in at the Windows dialog with something other than the password (PIN, fingerprint).</summary>
    PasswordNeeded,

    /// <summary>The system offers no way to check (no PAM).</summary>
    Unavailable,
}

/// <summary>
/// "Are you the person logged in here?" before something sensitive (connecting a phone): like an admin prompt, but with
/// the own computer login. Windows shows its own security dialog for the current account and checks the password
/// itself; on Linux and macOS Neruna asks for the password and lets the system check it (PAM, as at the login screen).
/// The password is never stored or logged.
/// </summary>
internal static class UserVerification
{
    /// <summary>The login name shown next to the password field.</summary>
    public static string UserName => Environment.UserName;

    /// <summary>Windows: the security dialog for the current account, over <paramref name="owner"/>.</summary>
    [SupportedOSPlatform("windows")]
    public static Task<UserCheck> AskWindowsAsync(IntPtr owner, string caption, string message) =>
        Task.Run(() => Windows.Ask(owner, caption, message));

    /// <summary>
    /// True when the current account has no password: then there is nothing to confirm with (Windows' dialog does not
    /// even accept an empty one) – whoever sits at such a computer is its user, by the owner's choice.
    /// </summary>
    public static Task<bool> HasNoPasswordAsync(ILogger? logger = null) =>
        Task.Run(() => OperatingSystem.IsWindows() ? Windows.HasNoPassword() : Pam.Check(UserName, string.Empty, logger) == UserCheck.Confirmed);

    /// <summary>Linux, macOS: checks the password of the current account with PAM.</summary>
    public static Task<UserCheck> CheckPasswordAsync(string password, ILogger? logger = null) =>
        Task.Run(() => Pam.Check(UserName, password, logger));

    [SupportedOSPlatform("windows")]
    private static class Windows
    {
        private const int CreduiwinEnumerateCurrentUser = 0x200;
        private const int ErrorCancelled = 1223;
        private const int LogonInteractive = 2;
        private const int ProviderDefault = 0;
        private const int ErrorAccountRestriction = 1327;

        // An empty password is refused outside the login screen ("Konten: Leere Kennwörter nur für Konsolenanmeldung
        // verwenden", on by default) with "account restriction" – which Windows only says once the password matched.
        // A wrong one says "wrong password". Without that policy the empty password simply signs in.
        public static bool HasNoPassword()
        {
            if (LogonUserW(Environment.UserName, Environment.UserDomainName, string.Empty, LogonInteractive, ProviderDefault, out var token))
            {
                using var identity = new WindowsIdentity(token);
                CloseHandle(token);
                using var current = WindowsIdentity.GetCurrent();
                return identity.User is not null && identity.User == current.User;
            }

            return Marshal.GetLastPInvokeError() == ErrorAccountRestriction;
        }

        public static UserCheck Ask(IntPtr owner, string caption, string message)
        {
            var info = new CredUiInfo
            {
                Size = Marshal.SizeOf<CredUiInfo>(),
                Parent = owner,
                MessageText = message,
                CaptionText = caption,
            };
            uint package = 0;
            var save = false;
            var result = CredUIPromptForWindowsCredentialsW(ref info, 0, ref package, IntPtr.Zero, 0, out var buffer, out var size, ref save, CreduiwinEnumerateCurrentUser);
            if (result == ErrorCancelled)
            {
                return UserCheck.Cancelled;
            }

            if (result != 0)
            {
                return UserCheck.Unavailable;
            }

            var user = new char[514];
            var domain = new char[338];
            var password = new char[256];
            var userLength = user.Length;
            var domainLength = domain.Length;
            var passwordLength = password.Length;
            try
            {
                if (!CredUnPackAuthenticationBufferW(0, buffer, size, user, ref userLength, domain, ref domainLength, password, ref passwordLength))
                {
                    return UserCheck.PasswordNeeded; // PIN or fingerprint: no password to check
                }
            }
            finally
            {
                ZeroMemory(buffer, size);
                Marshal.FreeCoTaskMem(buffer);
            }

            try
            {
                var name = Text(user);
                var at = name.IndexOf('\\', StringComparison.Ordinal);
                var (account, realm) = at > 0 ? (name[(at + 1)..], name[..at]) : (name, Text(domain) is { Length: > 0 } d ? d : null);
                var secret = Text(password);
                // Microsoft accounts: also as user@domain without "MicrosoftAccount\".
                if (!LogonUserW(account, realm, secret, LogonInteractive, ProviderDefault, out var token)
                    && !LogonUserW(account, null, secret, LogonInteractive, ProviderDefault, out token))
                {
                    return UserCheck.Wrong;
                }

                // The login must be the one of the person using Neruna right now.
                using var identity = new WindowsIdentity(token);
                CloseHandle(token);
                using var current = WindowsIdentity.GetCurrent();
                return identity.User is not null && identity.User == current.User ? UserCheck.Confirmed : UserCheck.Wrong;
            }
            finally
            {
                Array.Clear(password);
            }
        }

        private static string Text(char[] buffer) => new(buffer, 0, Math.Max(0, Array.IndexOf(buffer, '\0') is var end and >= 0 ? end : buffer.Length));

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CredUiInfo
        {
            public int Size;
            public IntPtr Parent;
            public string MessageText;
            public string CaptionText;
            public IntPtr Banner;
        }

        [DllImport("credui.dll", CharSet = CharSet.Unicode)]
        private static extern int CredUIPromptForWindowsCredentialsW(ref CredUiInfo info, int authError, ref uint authPackage, IntPtr inBuffer, int inSize,
            out IntPtr outBuffer, out int outSize, [MarshalAs(UnmanagedType.Bool)] ref bool save, int flags);

        [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredUnPackAuthenticationBufferW(int flags, IntPtr buffer, int size, [Out] char[] user, ref int userLength,
            [Out] char[] domain, ref int domainLength, [Out] char[] password, ref int passwordLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LogonUserW(string user, string? domain, string password, int logonType, int provider, out IntPtr token);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory")]
        private static extern void ZeroMemory(IntPtr destination, int length);
    }

    /// <summary>PAM (Linux-PAM, OpenPAM on macOS): the "login" service checks the password like the login screen.</summary>
    private static class Pam
    {
        private const int Success = 0;
        private const int PromptEchoOff = 1;
        private const int PromptEchoOn = 2;
        private const int ConversationError = 19;

        private delegate int Conversation(int count, IntPtr messages, out IntPtr responses, IntPtr data);

        public static UserCheck Check(string user, string password, ILogger? logger)
        {
            // PAM frees the answers with free(): they are allocated with malloc (AllocHGlobal on Unix).
            Conversation conversation = (int count, IntPtr messages, out IntPtr responses, IntPtr _) =>
            {
                responses = IntPtr.Zero;
                if (count <= 0)
                {
                    return ConversationError;
                }

                var size = (2 * IntPtr.Size);
                var answers = Marshal.AllocHGlobal(count * size);
                for (var i = 0; i < count; i++)
                {
                    var message = Marshal.ReadIntPtr(messages, i * IntPtr.Size);
                    var style = Marshal.ReadInt32(message);
                    var text = style == PromptEchoOff ? password : style == PromptEchoOn ? user : null;
                    Marshal.WriteIntPtr(answers, i * size, text is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(text));
                    Marshal.WriteIntPtr(answers, (i * size) + IntPtr.Size, IntPtr.Zero);
                }

                responses = answers;
                return Success;
            };

            var conv = new PamConv { Conversation = Marshal.GetFunctionPointerForDelegate(conversation) };
            try
            {
                byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");
                var started = OperatingSystem.IsMacOS()
                    ? MacPam.pam_start(Utf8("login"), Utf8(user), ref conv, out var handle)
                    : LinuxPam.pam_start(Utf8("login"), Utf8(user), ref conv, out handle);
                if (started != Success)
                {
                    logger?.LogWarning("PAM could not start ({Code})", started);
                    return UserCheck.Unavailable;
                }

                var result = OperatingSystem.IsMacOS() ? MacPam.pam_authenticate(handle, 0) : LinuxPam.pam_authenticate(handle, 0);
                _ = OperatingSystem.IsMacOS() ? MacPam.pam_end(handle, result) : LinuxPam.pam_end(handle, result);
                return result == Success ? UserCheck.Confirmed : UserCheck.Wrong;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                logger?.LogWarning("PAM is not available: {Message}", ex.Message);
                return UserCheck.Unavailable;
            }
            finally
            {
                GC.KeepAlive(conversation);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PamConv
        {
            public IntPtr Conversation;
            public IntPtr Data;
        }

        private static class LinuxPam
        {
            private const string Library = "libpam.so.0";

            [DllImport(Library)]
            public static extern int pam_start(byte[] service, byte[] user, ref PamConv conversation, out IntPtr handle);

            [DllImport(Library)]
            public static extern int pam_authenticate(IntPtr handle, int flags);

            [DllImport(Library)]
            public static extern int pam_end(IntPtr handle, int status);
        }

        private static class MacPam
        {
            private const string Library = "/usr/lib/libpam.dylib";

            [DllImport(Library)]
            public static extern int pam_start(byte[] service, byte[] user, ref PamConv conversation, out IntPtr handle);

            [DllImport(Library)]
            public static extern int pam_authenticate(IntPtr handle, int flags);

            [DllImport(Library)]
            public static extern int pam_end(IntPtr handle, int status);
        }
    }
}
