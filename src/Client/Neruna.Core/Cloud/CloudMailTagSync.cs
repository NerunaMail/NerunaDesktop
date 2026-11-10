using Microsoft.Extensions.Logging;
using Neruna.Core.Calendar;
using Neruna.Core.Mail;

namespace Neruna.Core.Cloud;

/// <summary>
/// Brings the organisations' tags and categories (Neruna Cloud → Schlagwörter &amp; Kategorien) into
/// <see cref="MailTagService"/> and <see cref="CalendarCategoryService"/>; the user's own are never touched. A server
/// without them (older version) simply has none. Without a category service (the app) only the tags.
/// </summary>
public sealed class CloudMailTagSync(CloudController cloud, MailTagService tags, ILogger<CloudMailTagSync> logger, CalendarCategoryService? categories = null)
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

            if (categories is not null)
            {
                var answer = await cloud.GetCalendarCategoriesAsync(c, cancellationToken);
                var given = answer?.Categories.Select(k => new CalendarCategory(k.Name, k.Color)).ToList() ?? [];
                if (await categories.SetCloudAsync(c.Source, given, cancellationToken))
                {
                    logger.LogInformation("Cloud categories of {Organization} updated: {Count} offered", c.OrganizationName, given.Count);
                    updated = true;
                }
            }

            return updated;
        }, logger, cancellationToken) | changed;
    }

    /// <summary>After disconnecting: the tags and categories of organisations no longer connected go too.</summary>
    public async Task<bool> RemoveDisconnectedAsync(CancellationToken cancellationToken = default)
    {
        var sources = (await cloud.GetConnectionsAsync(cancellationToken)).Select(c => c.Source).ToHashSet(StringComparer.Ordinal);
        var changed = false;
        foreach (var gone in (await tags.GetCloudSourcesAsync(cancellationToken)).Where(s => !sources.Contains(s)).ToList())
        {
            changed |= await tags.SetCloudAsync(gone, [], cancellationToken);
        }

        if (categories is not null)
        {
            foreach (var gone in (await categories.GetCloudSourcesAsync(cancellationToken)).Where(s => !sources.Contains(s)).ToList())
            {
                changed |= await categories.SetCloudAsync(gone, [], cancellationToken);
            }
        }

        return changed;
    }
}
