using Neruna.Core.Accounts;

namespace Neruna.Core.Mail;

/// <summary>
/// "Antwortadresse" (Reply-To) of a mail account: e.g. sent as info@ while answers go to the own address. Set by the
/// organisation (Neruna Cloud, kept with the account's mail connection – then it cannot be changed here), otherwise by
/// the user, kept on this device. Added to every mail sent from the account that has none of its own.
/// </summary>
public static class ReplyAddress
{
    /// <summary>The organisation's address, in the mail connection's settings (see the cloud account sync).</summary>
    public const string CloudSetting = "replyTo";

    private static string Key(Guid accountId) => $"mail.replyTo.{accountId:N}";

    /// <summary>The address that applies, and whether the organisation set it.</summary>
    public static async Task<(string? Address, bool FromCloud)> GetAsync(ISettingsStore? settings, Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.ConnectionsOf(ServiceKind.Mail).FirstOrDefault()?.Settings.GetValueOrDefault(CloudSetting) is { Length: > 0 } fromCloud)
        {
            return (fromCloud, true);
        }

        return (settings is null ? null : await settings.GetAsync(Key(account.Id), cancellationToken) is { Length: > 0 } own ? own : null, false);
    }

    /// <summary>The user's own address (empty or null: none).</summary>
    public static Task SetAsync(ISettingsStore settings, Guid accountId, string? address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.SetAsync(Key(accountId), string.IsNullOrWhiteSpace(address) ? null : address.Trim(), cancellationToken);
    }

    /// <summary>A single valid address (as the field takes it), or empty.</summary>
    public static bool IsValid(string? address) =>
        string.IsNullOrWhiteSpace(address)
        || (MimeKit.MailboxAddress.TryParse(address.Trim(), out var parsed) && parsed.Address == address.Trim() && address.Contains('@', StringComparison.Ordinal));
}
