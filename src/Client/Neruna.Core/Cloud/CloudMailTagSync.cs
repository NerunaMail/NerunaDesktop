using Microsoft.Extensions.Logging;
using Neruna.Core.Mail;

namespace Neruna.Core.Cloud;

/// <summary>
/// Brings the organisations' tags (Neruna Cloud → Schlagwörter) into <see cref="MailTagService"/>; the user's own tags
/// are never touched. A server without tags (older version) simply has none.
/// </summary>
public sealed class CloudMailTagSync(CloudController cloud, MailTagService tags, ILogger<CloudMailTagSync> logger)
{
    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
    {
        var connections = await cloud.GetConnectionsAsync(cancellationToken);
        var changed = await RemoveDisconnectedAsync(cancellationToken);
        return await CloudSync.EachAsync(connections, async c =>
        {
            var response = await cloud.GetMailTagsAsync(c, cancellationToken);
            var offered = response?.Tags.Select(t => new MailTag(t.Keyword, t.Name, t.Color)).ToList() ?? [];
            var updated = await tags.SetCloudAsync(c.Source, offered, cancellationToken);
            if (updated)
            {
                logger.LogInformation("Cloud tags of {Organization} updated: {Count} offered", c.OrganizationName, offered.Count);
            }

            return updated;
        }, logger, cancellationToken) | changed;
    }

    /// <summary>After disconnecting: the tags of organisations no longer connected go too.</summary>
    public async Task<bool> RemoveDisconnectedAsync(CancellationToken cancellationToken = default)
    {
        var sources = (await cloud.GetConnectionsAsync(cancellationToken)).Select(c => c.Source).ToHashSet(StringComparer.Ordinal);
        var changed = false;
        foreach (var gone in (await tags.GetCloudSourcesAsync(cancellationToken)).Where(s => !sources.Contains(s)).ToList())
        {
            changed |= await tags.SetCloudAsync(gone, [], cancellationToken);
        }

        return changed;
    }
}
