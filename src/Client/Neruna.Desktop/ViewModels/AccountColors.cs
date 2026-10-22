using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "⋯ → Farbe in der Ordnerliste": an account's folders on a light tint of its color, so the accounts of a long
/// list stand apart. Kept on this device only (the server knows nothing of it).
/// </summary>
internal static class AccountColors
{
    public static IReadOnlyList<string> Choices { get; } = ["#0F6CBD", "#038387", "#0B6A0B", "#8CBD18", "#C19C00", "#CA5010", "#D13438", "#E3008C", "#8764B8", "#69797E"];

    public static string Key(Guid accountId) => "mail.accountColor." + accountId.ToString("N");

    public static async Task<string?> GetAsync(ISettingsStore settings, Guid accountId) =>
        await settings.GetAsync(Key(accountId)) is { Length: > 0 } color && Color.TryParse(color, out _) ? color : null;

    public static Task SetAsync(ISettingsStore settings, Guid accountId, string? color) => settings.SetAsync(Key(accountId), color ?? string.Empty);

    /// <summary>The tint behind the account's folders: light enough for the text in both themes.</summary>
    public static IBrush? Tint(string? color) => color is not null && Color.TryParse(color, out var parsed) ? new SolidColorBrush(parsed, 0.13) : null;
}

internal sealed partial class AccountColorChoice(string? color) : ObservableObject
{
    public string? Color { get; } = color;

    public string Label => Color is null ? T("Keine Farbe") : string.Empty;

    public IBrush Swatch { get; } = color is null ? Brushes.Transparent : Brush.Parse(color);

    public bool HasColor => Color is not null;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    internal Action? Chosen { get; set; }

    [RelayCommand]
    private void Choose() => Chosen?.Invoke();
}
