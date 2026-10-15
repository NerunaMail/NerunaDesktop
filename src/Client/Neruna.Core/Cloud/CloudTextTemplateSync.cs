using Microsoft.Extensions.Logging;
using Neruna.Core.Mail;

namespace Neruna.Core.Cloud;

/// <summary>
/// Brings the organisation's text templates into the local list (source "cloud"), like <see cref="CloudSignatureSync"/>
/// for signatures: added, updated, removed; the user's own templates are never touched.
/// </summary>
public sealed class CloudTextTemplateSync(CloudController cloud, TextTemplateService templates, ILogger<CloudTextTemplateSync> logger)
{
    public event EventHandler? Changed;

    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
    {
        var connections = await cloud.GetConnectionsAsync(cancellationToken);
        var changed = await RemoveDisconnectedAsync(cancellationToken);
        return await CloudSync.EachAsync(connections, c => SyncAsync(c, cancellationToken), logger, cancellationToken) | changed;
    }

    private async Task<bool> SyncAsync(CloudConnection connection, CancellationToken cancellationToken)
    {
        var response = await cloud.GetTextTemplatesAsync(connection, cancellationToken);
        var existing = (await templates.GetAllAsync(cancellationToken)).Where(t => t.Source == connection.Source).ToDictionary(t => t.Id);
        var changed = false;
        foreach (var offered in response.Templates)
        {
            var id = LocalId(offered.Id, connection);
            var shortcut = TextTemplate.NormalizeShortcut(offered.Shortcut);
            if (existing.Remove(id, out var current) && current.Name == offered.Name && current.Html == offered.Html && current.Shortcut == shortcut)
            {
                continue;
            }

            await templates.SaveAsync(new TextTemplate(id, offered.Name, offered.Html, offered.UpdatedAt, connection.Source, shortcut), cancellationToken);
            changed = true;
        }

        foreach (var gone in existing.Values)
        {
            await templates.DeleteAsync(gone.Id, cancellationToken);
            changed = true;
        }

        if (changed)
        {
            logger.LogInformation("Cloud text templates of {Organization} updated: {Count} offered", connection.OrganizationName, response.Templates.Count);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>After disconnecting: the templates of organisations no longer connected go too.</summary>
    public async Task<bool> RemoveDisconnectedAsync(CancellationToken cancellationToken = default)
    {
        var sources = (await cloud.GetConnectionsAsync(cancellationToken)).Select(c => c.Source).ToHashSet(StringComparer.Ordinal);
        var gone = (await templates.GetAllAsync(cancellationToken)).Where(t => t.IsFromCloud && !sources.Contains(t.Source)).ToList();
        foreach (var template in gone)
        {
            await templates.DeleteAsync(template.Id, cancellationToken);
        }

        if (gone.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return gone.Count > 0;
    }

    public static Guid LocalId(string cloudId, CloudConnection? connection = null) => CloudSync.LocalId("neruna-cloud-text-template", connection, cloudId);
}
