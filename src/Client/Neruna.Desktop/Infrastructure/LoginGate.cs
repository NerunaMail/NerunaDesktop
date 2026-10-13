using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// "Bitte mit dem Login dieses Computers bestätigen" before something sensitive (connecting a phone, moving an account
/// to it): Windows asks in its security dialog, Linux and macOS in the page (<c>Views/LoginGateView</c>), see
/// <see cref="UserVerification"/>.
/// </summary>
internal sealed partial class LoginGate : ObservableObject
{
    private TaskCompletionSource<bool>? _waiting;

    /// <summary>Linux/macOS: the password field is shown.</summary>
    [ObservableProperty]
    public partial bool IsAsking { get; set; }

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Prompt { get; set; }

    /// <summary>Why it was not confirmed (wrong password, PIN on Windows, nothing to check with).</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    public string Label { get; } = F("Passwort von «{0}» auf diesem Computer", UserVerification.UserName);

    /// <summary>True once the person at the computer confirmed with its login.</summary>
    public async Task<bool> VerifyAsync(string prompt)
    {
        Error = null;
        Prompt = prompt;
        if (OperatingSystem.IsWindows())
        {
            var owner = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            return Judge(await UserVerification.AskWindowsAsync(owner, T("Bestätigung mit dem Computer-Login"), prompt));
        }

        _waiting?.TrySetResult(false);
        _waiting = new TaskCompletionSource<bool>();
        Password = string.Empty;
        IsAsking = true;
        return await _waiting.Task;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        var password = Password;
        Password = string.Empty;
        if (password.Length == 0 || _waiting is not { } waiting)
        {
            return;
        }

        var confirmed = Judge(await UserVerification.CheckPasswordAsync(password));
        if (confirmed || Error is not null && !IsWrong)
        {
            IsAsking = false;
            _waiting = null;
            waiting.TrySetResult(confirmed);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        IsAsking = false;
        Password = string.Empty;
        Error = null;
        _waiting?.TrySetResult(false);
        _waiting = null;
    }

    private bool IsWrong { get; set; }

    private bool Judge(UserCheck check)
    {
        IsWrong = check == UserCheck.Wrong;
        Error = check switch
        {
            UserCheck.Confirmed or UserCheck.Cancelled => null,
            UserCheck.Wrong => T("Das Passwort stimmt nicht."),
            UserCheck.PasswordNeeded => T("Bitte mit dem Passwort des Windows-Kontos bestätigen (unter «Weitere Optionen») – PIN und Fingerabdruck können hier nicht geprüft werden."),
            _ => T("Das Computer-Login kann auf diesem System nicht geprüft werden."),
        };
        return check == UserCheck.Confirmed;
    }
}
