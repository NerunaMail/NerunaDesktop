using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// The full name of the person logged in to the computer ("Anna Muster", not the login "amuster") – for stamps, so an
/// info mailbox does not put its own name on them. Windows: the display name of the domain account, else the full name
/// of the local account; macOS: the account's full name; Linux: the name field of /etc/passwd. The login otherwise.
/// </summary>
internal static class SystemUserName
{
    private static string? _cached;

    public static string Get() => _cached ??= Find() is { Length: > 0 } name ? name : Environment.UserName;

    private static string? Find()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Windows.DisplayName() ?? Windows.LocalFullName();
            }

            if (OperatingSystem.IsMacOS())
            {
                using var process = Process.Start(new ProcessStartInfo("id", "-F") { RedirectStandardOutput = true, UseShellExecute = false });
                var name = process?.StandardOutput.ReadToEnd().Trim();
                process?.WaitForExit(2000);
                return name;
            }

            // Linux: "amuster:x:1000:1000:Anna Muster,,,:/home/amuster:/bin/bash" – the name is the start of field 5.
            var line = File.ReadLines("/etc/passwd").FirstOrDefault(l => l.StartsWith(Environment.UserName + ":", StringComparison.Ordinal));
            var fields = line?.Split(':');
            return fields is { Length: > 4 } ? fields[4].Split(',')[0].Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static class Windows
    {
        private const int NameDisplay = 3;

        public static string? DisplayName()
        {
            var size = 256u;
            var buffer = new char[size];
            return GetUserNameExW(NameDisplay, buffer, ref size) ? new string(buffer, 0, (int)size).Trim() : null;
        }

        // A local account (also one signed in with a Microsoft account): its full name.
        public static string? LocalFullName()
        {
            if (NetUserGetInfo(null, Environment.UserName, 10, out var info) != 0 || info == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                // USER_INFO_10: name, comment, usr_comment, full_name – four string pointers.
                return Marshal.PtrToStringUni(Marshal.ReadIntPtr(info, 3 * IntPtr.Size))?.Trim();
            }
            finally
            {
                _ = NetApiBufferFree(info);
            }
        }

        [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool GetUserNameExW(int format, [Out] char[] buffer, ref uint size);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr info);

        [DllImport("netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr buffer);
    }
}
