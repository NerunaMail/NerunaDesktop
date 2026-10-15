using Microsoft.Extensions.Logging;

namespace Neruna.Core.Cloud;

/// <summary>What every sync with the organisations shares: each connection on its own.</summary>
internal static class CloudSync
{
    /// <summary>
    /// Runs <paramref name="sync"/> for every connection; one that cannot be reached does not hold up the others – its
    /// error is thrown once they are done.
    /// </summary>
    /// <returns>Whether any of them changed something.</returns>
    public static async Task<bool> EachAsync(IEnumerable<CloudConnection> connections, Func<CloudConnection, Task<bool>> sync, ILogger logger, CancellationToken cancellationToken)
    {
        var changed = false;
        Exception? failed = null;
        foreach (var connection in connections)
        {
            try
            {
                changed |= await sync(connection);
            }
            catch (Exception ex) when (ex is CloudException or HttpRequestException || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Sync with {Server} ({Organization}) failed", connection.Server.Host, connection.OrganizationName);
                failed ??= ex;
            }
        }

        if (failed is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
        }

        return changed;
    }

    /// <summary>The same local id for something of the organisation on every refresh (first connection: as before).</summary>
    public static Guid LocalId(string purpose, CloudConnection? connection, string cloudId)
    {
        var name = connection is null || connection.IsFirst ? $"{purpose}:{cloudId}" : $"{purpose}:{connection.Id}:{cloudId}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)).AsSpan(0, 16).ToArray();
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80); // RFC 9562 version 8 (custom, name-based)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash, bigEndian: true);
    }
}
