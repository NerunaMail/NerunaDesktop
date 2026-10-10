using Microsoft.Extensions.Logging;
using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Core.Security;

namespace Neruna.Providers.Imap;

/// <param name="protocolLogDirectory">If set, raw IMAP/SMTP traces (passwords redacted) are written there.</param>
/// <param name="maxInitialMessages">How many of the newest messages the first sync of a folder fetches.</param>
public sealed class ImapProviderFactory(
    ICredentialStore credentials,
    ILoggerFactory loggerFactory,
    string? protocolLogDirectory = null,
    int maxInitialMessages = ImapMailProvider.DefaultMaxInitialMessages) : IProviderFactory<IMailProvider>
{
    public string ProviderId => ProviderIds.Imap;

    public string DisplayName => "IMAP / SMTP";

    public IMailProvider Create(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new ImapMailProvider(
            connection.Id,
            ImapSettings.FromDictionary(connection.Settings),
            credentials,
            loggerFactory.CreateLogger<ImapMailProvider>(),
            protocolLogDirectory,
            maxInitialMessages);
    }

    public IReadOnlyDictionary<string, string>? SettingsFromDiscovery(MailProviderConfig config, string emailAddress) =>
        ImapSettings.FromDiscovery(config, emailAddress)?.ToDictionary();

    public MailProviderConfig? DescribeSettings(IReadOnlyDictionary<string, string> settings)
    {
        var imap = ImapSettings.FromDictionary(settings);
        return new MailProviderConfig(string.Empty, null,
            [new MailServerSettings(ServerProtocol.Imap, imap.ImapHost, imap.ImapPort, imap.ImapSecurity, AuthScheme.PasswordCleartext, imap.Username)],
            [new MailServerSettings(ServerProtocol.Smtp, imap.SmtpHost, imap.SmtpPort, imap.SmtpSecurity, AuthScheme.PasswordCleartext, imap.Username)],
            []);
    }

    public string Summary(IReadOnlyDictionary<string, string> settings)
    {
        var imap = ImapSettings.FromDictionary(settings);
        return $"{imap.ImapHost}:{imap.ImapPort} · SMTP {imap.SmtpHost}:{imap.SmtpPort} · {imap.Username}";
    }
}
