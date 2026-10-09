using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Neruna.Core.Mail;

namespace Neruna.Core.Cloud;

/// <summary>
/// Brings the organisation's central signatures into the local signature list (source "cloud"): new ones added,
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
        if (await cloud.GetConnectionAsync(cancellationToken) is null)
        {
            return await RemoveAllAsync(cancellationToken);
        }

        var response = await cloud.GetSignaturesAsync(cancellationToken);
        var existing = (await signatures.GetAllAsync(cancellationToken)).Where(s => s.IsFromCloud).ToDictionary(s => s.Id);
        var changed = false;

        foreach (var offered in response.Signatures)
        {
            var id = LocalId(offered.Id);
            if (existing.Remove(id, out var current) && current.Name == offered.Name && current.Html == offered.Html)
            {
                continue;
            }

            await signatures.SaveAsync(new Signature(id, offered.Name, offered.Html, offered.UpdatedAt, Signature.CloudSource), cancellationToken);
            changed = true;
        }

        foreach (var gone in existing.Values)
        {
            await signatures.DeleteAsync(gone.Id, cancellationToken);
            changed = true;
        }

        if (changed)
        {
            logger.LogInformation("Cloud signatures updated: {Count} offered", response.Signatures.Count);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>After disconnecting: the organisation's signatures go too.</summary>
    public async Task<bool> RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        var cloudSignatures = (await signatures.GetAllAsync(cancellationToken)).Where(s => s.IsFromCloud).ToList();
        foreach (var signature in cloudSignatures)
        {
            await signatures.DeleteAsync(signature.Id, cancellationToken);
        }

        if (cloudSignatures.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return cloudSignatures.Count > 0;
    }

    /// <summary>The same local id for a cloud signature on every refresh (keeps the per-account choices).</summary>
    public static Guid LocalId(string cloudId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("neruna-cloud-signature:" + cloudId)).AsSpan(0, 16).ToArray();
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80); // RFC 9562 version 8 (custom, name-based)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash, bigEndian: true);
    }
}
