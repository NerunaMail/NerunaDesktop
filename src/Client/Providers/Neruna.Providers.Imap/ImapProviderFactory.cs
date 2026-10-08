using Microsoft.Extensions.Logging;
using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Core.Security;

namespace Neruna.Providers.Imap;

/// <param name="protocolLogDirectory">If set, raw IMAP/SMTP traces (passwords redacted) are written there.</param>
public sealed class ImapProviderFactory(ICredentialStore credentials, ILoggerFactory loggerFactory, string? protocolLogDirectory = null) : IProviderFactory<IMailProvider>
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
            protocolLogDirectory);
    }

    public IReadOnlyDictionary<string, string>? SettingsFromDiscovery(MailProviderConfig config, string emailAddress) =>
        ImapSettings.FromDiscovery(config, emailAddress)?.ToDictionary();
}
