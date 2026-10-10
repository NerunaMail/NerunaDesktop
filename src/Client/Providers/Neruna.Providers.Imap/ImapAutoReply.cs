using System.Net.Sockets;
using Neruna.Contracts.Discovery;
using Neruna.Core.Mail;
using Neruna.Providers.Imap.Sieve;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Imap;

/// <summary>
/// The out-of-office reply over ManageSieve (RFC 5804, port 4190 on the IMAP host) – the standard way for IMAP servers
/// (Dovecot/Pigeonhole, Cyrus, Stalwart …). Provider-specific ways (e.g. SOGo's own settings) can be added later.
/// </summary>
public sealed partial class ImapMailProvider
{
    /// <summary>The ManageSieve port (RFC 5804).</summary>
    public const int SievePort = 4190;

    // A script name of our own when the user has no active script yet.
    private const string OwnScript = "neruna";

    public async Task<(AutoReply Reply, AutoReplyFeatures Features)> GetAutoReplyAsync(CancellationToken cancellationToken = default)
    {
        await using var sieve = await OpenSieveAsync(cancellationToken);
        var active = (await sieve.ListScriptsAsync(cancellationToken)).FirstOrDefault(s => s.Active).Name;
        var script = active is null ? null : await sieve.GetScriptAsync(active, cancellationToken);
        return (SieveVacation.Read(script), FeaturesOf(sieve));
    }

    public async Task SetAutoReplyAsync(AutoReply reply, IReadOnlyList<string> ownAddresses, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reply);
        await using var sieve = await OpenSieveAsync(cancellationToken);
        var withDates = FeaturesOf(sieve).HasFlag(AutoReplyFeatures.Schedule);
        if (reply.IsEnabled && reply.IsScheduled && !withDates)
        {
            throw new AutoReplyUnavailableException(T("Dieser Server kann die Abwesenheitsnotiz nicht auf einen Zeitraum beschränken. Bitte ohne Zeitraum einschalten und danach wieder ausschalten."));
        }

        var active = (await sieve.ListScriptsAsync(cancellationToken)).FirstOrDefault(s => s.Active).Name;
        var script = active is null ? null : await sieve.GetScriptAsync(active, cancellationToken);
        try
        {
            // Into the active script (the user's filters stay), or a new one of our own.
            await sieve.PutScriptAsync(active ?? OwnScript, SieveVacation.Write(script, reply, ownAddresses, withDates), cancellationToken);
            if (active is null)
            {
                await sieve.SetActiveAsync(OwnScript, cancellationToken);
            }
        }
        catch (ManageSieveException ex)
        {
            throw new AutoReplyUnavailableException(F("Der Server hat die Abwesenheitsnotiz abgelehnt: {0}", ex.Message), ex);
        }
    }

    private static AutoReplyFeatures FeaturesOf(ManageSieveClient sieve) =>
        AutoReplyFeatures.Subject
        | (sieve.Extensions.Contains("date") && sieve.Extensions.Contains("relational") ? AutoReplyFeatures.Schedule : AutoReplyFeatures.None);

    private async Task<ManageSieveClient> OpenSieveAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        ManageSieveClient? sieve = null;
        try
        {
            // Plain text only where IMAP itself is unencrypted (a local test server); otherwise TLS or nothing.
            sieve = await ManageSieveClient.ConnectAsync(settings.ImapHost, SievePort, allowPlainText: settings.ImapSecurity == SocketSecurity.None, timeout.Token);
            if (!sieve.Extensions.Contains("vacation"))
            {
                throw new AutoReplyUnavailableException(T("Der Mailserver kann keine Abwesenheitsnotiz verschicken (Sieve ohne «vacation»)."));
            }

            await sieve.AuthenticateAsync(settings.Username, await GetPasswordAsync(cancellationToken), timeout.Token);
            return sieve;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException && !cancellationToken.IsCancellationRequested)
        {
            if (sieve is not null)
            {
                await sieve.DisposeAsync();
            }

            throw new AutoReplyUnavailableException(F("Der Mailserver bietet keine Abwesenheitsnotiz an – ManageSieve (Port {0}) auf {1} ist nicht erreichbar.", SievePort, settings.ImapHost), ex);
        }
        catch (ManageSieveException ex)
        {
            if (sieve is not null)
            {
                await sieve.DisposeAsync();
            }

            throw new AutoReplyUnavailableException(F("Anmeldung für die Abwesenheitsnotiz fehlgeschlagen: {0}", ex.Message), ex);
        }
        catch
        {
            if (sieve is not null)
            {
                await sieve.DisposeAsync();
            }

            throw;
        }
    }
}
