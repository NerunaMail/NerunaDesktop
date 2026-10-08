using System.Reflection;
using System.Runtime.InteropServices;

namespace Neruna.Core.Cloud;

/// <summary>What the portal shows about this computer, so a person's devices can be told apart.</summary>
public sealed record DeviceInfo(string Name, string AppVersion, string OsName, string OsVersion, string OsUser)
{
    public static DeviceInfo Current(string? name = null)
    {
        var os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : RuntimeInformation.OSDescription;
        // macOS: "15.1" rather than the Darwin kernel version .NET reports; Linux: the distribution ("Debian GNU/Linux 12 …").
        var version = OperatingSystem.IsWindows() ? WindowsVersion()
            : OperatingSystem.IsMacOS() ? Environment.OSVersion.Version.ToString(2)
            : RuntimeInformation.OSDescription.Trim();
        var user = OperatingSystem.IsWindows() && !string.IsNullOrEmpty(Environment.UserDomainName)
            ? $@"{Environment.UserDomainName}\{Environment.UserName}"
            : Environment.UserName;
        var app = (Assembly.GetEntryAssembly() ?? typeof(DeviceInfo).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";
        return new DeviceInfo(string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim(), app, os, version, user);
    }

    // "11 (Build 26100)": Windows 11 reports itself as 10.0 with a build of 22000 or higher.
    private static string WindowsVersion()
    {
        var v = Environment.OSVersion.Version;
        var major = v.Major == 10 && v.Build >= 22000 ? "11" : v.Major == 10 ? "10" : $"{v.Major}.{v.Minor}";
        return $"{major} (Build {v.Build})";
    }
}
