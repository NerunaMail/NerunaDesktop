using Microsoft.Extensions.Logging;
using Neruna.Core.Mail;

namespace Neruna.Core.Cloud;

/// <summary>
/// Brings the organisations' central signatures into the local signature list (source "cloud"): new ones added,
/// changed ones replaced, those no longer offered removed. The user's own signatures are never touched. A cloud
/// signature keeps the same local id, so per-account default choices stay.
/// </summary>
public sealed class CloudSignatureSync(CloudController cloud, SignatureService signatures, ILogger<CloudSignatureSync> logger)
{
    /// <summary>The signature list changed (the settings page reloads).</summary>
    public event EventHandler? Changed;

    /// <returns>Whether anything changed.</returns>
    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
    {
        var connections = await cloud.GetConnectionsAsync(cancellationToken);
        var changed = await RemoveDisconnectedAsync(cancellationToken);
        return await CloudSync.EachAsync(connections, c => SyncAsync(c, cancellationToken), logger, cancellationToken) | changed;
    }

    private async Task<bool> SyncAsync(CloudConnection connection, CancellationToken cancellationToken)
    {
        var response = await cloud.GetSignaturesAsync(connection, cancellationToken);
        var existing = (await signatures.GetAllAsync(cancellationToken)).Where(s => s.Source == connection.Source).ToDictionary(s => s.Id);
        var changed = false;

        foreach (var offered in response.Signatures)
        {
            var id = LocalId(offered.Id, connection);
            if (existing.Remove(id, out var current) && current.Name == offered.Name && current.Html == offered.Html)
            {
                continue;
            }

            await signatures.SaveAsync(new Signature(id, offered.Name, offered.Html, offered.UpdatedAt, connection.Source), cancellationToken);
            changed = true;
        }

        foreach (var gone in existing.Values)
        {
            await signatures.DeleteAsync(gone.Id, cancellationToken);
            changed = true;
        }

        if (changed)
        {
            logger.LogInformation("Cloud signatures of {Organization} updated: {Count} offered", connection.OrganizationName, response.Signatures.Count);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>After disconnecting: the signatures of organisations no longer connected go too.</summary>
    public async Task<bool> RemoveDisconnectedAsync(CancellationToken cancellationToken = default)
    {
        var sources = (await cloud.GetConnectionsAsync(cancellationToken)).Select(c => c.Source).ToHashSet(StringComparer.Ordinal);
        var gone = (await signatures.GetAllAsync(cancellationToken)).Where(s => s.IsFromCloud && !sources.Contains(s.Source)).ToList();
        foreach (var signature in gone)
        {
            await signatures.DeleteAsync(signature.Id, cancellationToken);
        }

        if (gone.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return gone.Count > 0;
    }

    /// <summary>The same local id for a cloud signature on every refresh (keeps the per-account choices).</summary>
    public static Guid LocalId(string cloudId, CloudConnection? connection = null) => CloudSync.LocalId("neruna-cloud-signature", connection, cloudId);
}
