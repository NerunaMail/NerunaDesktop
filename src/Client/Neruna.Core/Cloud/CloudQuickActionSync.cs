using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Neruna.Contracts.Cloud;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;

namespace Neruna.Core.Cloud;

/// <summary>
/// Quick actions shared in the organisation: brings them into the own list (fitted to the accounts and templates here,
/// a switched-off one stays off), shares one and saves new versions of shared ones. Who may share (set in the portal)
/// may also change those others shared. Actions no longer offered (deleted, switched off, not for this person) go.
/// </summary>
public sealed class CloudQuickActionSync(CloudController cloud, QuickActionService actions, IAccountStore accounts, TextTemplateService templates, ILogger<CloudQuickActionSync> logger)
{
    private readonly ConcurrentDictionary<string, bool> _canShare = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    /// <summary>A connection lets this person share (as of the last sync).</summary>
    public bool CanShareAny => _canShare.Values.Any(v => v);

    /// <summary>This person may change <paramref name="action"/>: an own one, or a shared one with the right to share.</summary>
    public bool CanEdit(QuickAction action) => action.Shared is not { } shared || _canShare.GetValueOrDefault(shared.Source);

    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
    {
        var connections = await cloud.GetConnectionsAsync(cancellationToken);
        var changed = await RemoveDisconnectedAsync(cancellationToken);
        return await CloudSync.EachAsync(connections, c => SyncAsync(c, cancellationToken), logger, cancellationToken) | changed;
    }

    private async Task<bool> SyncAsync(CloudConnection connection, CancellationToken cancellationToken)
    {
        var response = await cloud.GetQuickActionsAsync(connection, cancellationToken);
        if (response is null)
        {
            return false; // an older server: nothing shared there
        }

        _canShare[connection.Source] = response.CanShare;
        var list = (await actions.GetActionsAsync(cancellationToken)).ToList();
        var stamps = (await actions.GetStampsAsync(cancellationToken)).ToList();
        var (accountAddresses, templateNames) = await LookupsAsync(cancellationToken);
        var changed = false;
        var offeredIds = new HashSet<Guid>();
        foreach (var offered in response.Actions)
        {
            if (!Guid.TryParse(offered.Id, out var id))
            {
                continue;
            }

            QuickAction action;
            try
            {
                var (imported, importedStamps) = QuickActionService.Import(offered.Content, accountAddresses, templateNames, shared: true);
                if (imported.Count == 0)
                {
                    continue;
                }

                action = imported[0];
                foreach (var stamp in importedStamps.Where(s => stamps.All(here => here.Id != s.Id)))
                {
                    stamps.Add(stamp);
                    await actions.SaveStampsAsync(stamps, cancellationToken);
                }
            }
            catch (FormatException ex)
            {
                logger.LogWarning(ex, "Shared quick action {Id} not readable", offered.Id);
                continue;
            }

            offeredIds.Add(id);
            var index = list.FindIndex(a => a.Id == id);
            action = action with
            {
                Id = id,
                Shared = new QuickActionShare(connection.Source, offered.SharedBy, offered.UpdatedBy, offered.UpdatedAt),
                IsOff = index >= 0 && list[index].IsOff,
            };
            if (index < 0)
            {
                list.Add(action);
                changed = true;
            }
            else if (!QuickActionService.Same(list[index], action))
            {
                list[index] = action;
                changed = true;
            }
        }

        changed |= list.RemoveAll(a => a.Shared?.Source == connection.Source && !offeredIds.Contains(a.Id)) > 0;
        if (changed)
        {
            await actions.SaveActionsAsync(list, cancellationToken);
            logger.LogInformation("Shared quick actions of {Organization} updated: {Count} offered", connection.OrganizationName, response.Actions.Count);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>After disconnecting: the shared actions of organisations no longer connected go too.</summary>
    public async Task<bool> RemoveDisconnectedAsync(CancellationToken cancellationToken = default)
    {
        var sources = (await cloud.GetConnectionsAsync(cancellationToken)).Select(c => c.Source).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _canShare.Keys.Where(k => !sources.Contains(k)).ToList())
        {
            _canShare.TryRemove(gone, out _);
        }

        var list = (await actions.GetActionsAsync(cancellationToken)).ToList();
        if (list.RemoveAll(a => a.Shared is { } shared && !sources.Contains(shared.Source)) == 0)
        {
            return false;
        }

        await actions.SaveActionsAsync(list, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Shares <paramref name="action"/> (with the first connection that allows it), or saves its new version when it is
    /// shared already. The stored action gets where it is shared.
    /// </summary>
    /// <exception cref="CloudException">Not allowed (any more) or the server said no.</exception>
    public async Task<QuickAction> ShareAsync(QuickAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var connections = await cloud.GetConnectionsAsync(cancellationToken);
        var connection = action.Shared is { } shared
            ? connections.FirstOrDefault(c => c.Source == shared.Source)
            : connections.FirstOrDefault(c => _canShare.GetValueOrDefault(c.Source));
        if (connection is null)
        {
            throw new CloudException("not_allowed", "No connection allows sharing quick actions");
        }

        var (accountAddresses, templateNames) = await LookupsAsync(cancellationToken);
        var content = QuickActionService.Export([action], await actions.GetStampsAsync(cancellationToken), accountAddresses, templateNames);
        var stored = await cloud.ShareQuickActionAsync(connection, action.Id.ToString(), new ShareQuickActionRequest(action.Label, content), cancellationToken);
        var result = action with { Shared = new QuickActionShare(connection.Source, stored.SharedBy, stored.UpdatedBy, stored.UpdatedAt) };
        await ReplaceAsync(result, cancellationToken);
        return result;
    }

    /// <summary>Stops sharing: gone for everyone else; here it stays as an own action.</summary>
    public async Task<QuickAction> UnshareAsync(QuickAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Shared is { } shared && (await cloud.GetConnectionsAsync(cancellationToken)).FirstOrDefault(c => c.Source == shared.Source) is { } connection)
        {
            await cloud.UnshareQuickActionAsync(connection, action.Id.ToString(), cancellationToken);
        }

        var result = action with { Shared = null };
        await ReplaceAsync(result, cancellationToken);
        return result;
    }

    private async Task ReplaceAsync(QuickAction action, CancellationToken cancellationToken)
    {
        var list = (await actions.GetActionsAsync(cancellationToken)).ToList();
        var index = list.FindIndex(a => a.Id == action.Id);
        if (index >= 0)
        {
            list[index] = action;
        }
        else
        {
            list.Add(action);
        }

        await actions.SaveActionsAsync(list, cancellationToken);
    }

    private async Task<(Dictionary<Guid, string> Accounts, Dictionary<Guid, string> Templates)> LookupsAsync(CancellationToken cancellationToken) =>
        ((await accounts.GetAccountsAsync(cancellationToken)).Where(a => a.EmailAddress is not null).ToDictionary(a => a.Id, a => a.EmailAddress!),
         (await templates.GetAllAsync(cancellationToken)).ToDictionary(t => t.Id, t => t.Name));
}
