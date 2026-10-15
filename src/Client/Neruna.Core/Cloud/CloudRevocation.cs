using Microsoft.Extensions.Logging;
using Neruna.Core.Chat;

namespace Neruna.Core.Cloud;

/// <summary>
/// Remote wipe: when an organisation blocks or deletes this device in the portal, the next request to it is refused
/// ("device_revoked") – then everything that organisation brought leaves this device: its accounts (with passwords and
/// their downloaded mail), certificates with their private keys, signatures, text templates, the key to its backup
/// vault and its chat. Own accounts and other organisations' things stay. Created once at start (the app resolves it).
/// </summary>
public sealed class CloudRevocation
{
    public CloudRevocation(
        CloudController cloud,
        CloudSignatureSync signatures,
        CloudTextTemplateSync textTemplates,
        CloudCertificateSync certificates,
        CloudAccountSync accounts,
        SettingsBackupService backup,
        ILogger<CloudRevocation> logger,
        ChatController? chat = null)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        cloud.OnRevoked(async (connection, cancellationToken) =>
        {
            // The connection is already gone: each of these removes what no remaining connection owns.
            await accounts.RemoveDisconnectedAsync(cancellationToken);
            await certificates.RemoveDisconnectedAsync(cancellationToken);
            await signatures.RemoveDisconnectedAsync(cancellationToken);
            await textTemplates.RemoveDisconnectedAsync(cancellationToken);
            await backup.ForgetAsync(connection, cancellationToken);
            if (chat is not null)
            {
                // Right after, not inside: the chat's own refresh may be the request that noticed it (and holds its lock).
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await chat.ForgetAsync(connection, CancellationToken.None);

                        // Another organisation's chat now, or none.
                        await chat.RefreshAsync(CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is CloudException or HttpRequestException)
                    {
                        logger.LogInformation(ex, "Chat not refreshed after the device was blocked");
                    }
                }, CancellationToken.None);
            }
        });
    }
}
