using System.Security.Cryptography;
using System.Text;
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
        if (await cloud.GetConnectionAsync(cancellationToken) is null)
        {
            return await RemoveAllAsync(cancellationToken);
        }

        var response = await cloud.GetTextTemplatesAsync(cancellationToken);
        var existing = (await templates.GetAllAsync(cancellationToken)).Where(t => t.IsFromCloud).ToDictionary(t => t.Id);
        var changed = false;
        foreach (var offered in response.Templates)
        {
            var id = LocalId(offered.Id);
            var shortcut = TextTemplate.NormalizeShortcut(offered.Shortcut);
            if (existing.Remove(id, out var current) && current.Name == offered.Name && current.Html == offered.Html && current.Shortcut == shortcut)
            {
                continue;
            }

            await templates.SaveAsync(new TextTemplate(id, offered.Name, offered.Html, offered.UpdatedAt, Signature.CloudSource, shortcut), cancellationToken);
            changed = true;
        }

        foreach (var gone in existing.Values)
        {
            await templates.DeleteAsync(gone.Id, cancellationToken);
            changed = true;
        }

        if (changed)
        {
            logger.LogInformation("Cloud text templates updated: {Count} offered", response.Templates.Count);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    public async Task<bool> RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        var cloudTemplates = (await templates.GetAllAsync(cancellationToken)).Where(t => t.IsFromCloud).ToList();
        foreach (var template in cloudTemplates)
        {
            await templates.DeleteAsync(template.Id, cancellationToken);
        }

        if (cloudTemplates.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return cloudTemplates.Count > 0;
    }

    public static Guid LocalId(string cloudId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("neruna-cloud-text-template:" + cloudId)).AsSpan(0, 16).ToArray();
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash, bigEndian: true);
    }
}
