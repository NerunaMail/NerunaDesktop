using Microsoft.Extensions.Logging;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Mail;

namespace Neruna.Core;

/// <summary>
/// The one way to sync everything (mail, calendar, contacts). Never two runs at once – calendar and contacts have no
/// locks of their own. A request while a run is going waits for the next run; all requests waiting meanwhile share
/// that one run, so a busy timer, the sync button and a new account do not pile up full syncs.
/// </summary>
/// <param name="certificates">Moves other people's certificates to their contacts after a run (null: not at all).</param>
public sealed class SyncCoordinator(MailController mail, CalendarController calendar, ContactController contacts, Security.CertificateManager? certificates = null,
    ILogger<SyncCoordinator>? logger = null) : IDisposable
{
    // Moving reads all contacts: not after every run.
    private static readonly TimeSpan MoveInterval = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _movedAt = DateTimeOffset.MinValue;
    private long _requested;
    private long _coveredUpTo;
    private IReadOnlyList<SyncReport> _last = [];

    /// <returns>The reports of the run that covered this request (it started after the request).</returns>
    public async Task<IReadOnlyList<SyncReport>> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var ticket = Interlocked.Increment(ref _requested);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // A run that started after this request finished while we waited: nothing new to fetch.
            if (Interlocked.Read(ref _coveredUpTo) >= ticket)
            {
                return _last;
            }

            var covers = Interlocked.Read(ref _requested);
            // The three areas are independent; one failing never blocks the others. Off the caller's thread: SQLite
            // and parsing hundreds of messages, events and cards would freeze a window.
            _last = await Task.Run(() => Task.WhenAll(mail.SyncAllAsync(cancellationToken), calendar.SyncAllAsync(cancellationToken), contacts.SyncAllAsync(cancellationToken)), cancellationToken);
            Interlocked.Exchange(ref _coveredUpTo, covers);
            await MoveCertificatesAsync(cancellationToken);
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Certificates kept on this device whose person now has a contact (added, or synced from another device).
    private async Task MoveCertificatesAsync(CancellationToken cancellationToken)
    {
        if (certificates is null || DateTimeOffset.UtcNow - _movedAt < MoveInterval)
        {
            return;
        }

        _movedAt = DateTimeOffset.UtcNow;
        try
        {
            await Task.Run(() => certificates.MoveToContactsAsync(cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Next time; the certificates stay usable where they are.
            logger?.LogInformation(ex, "Moving certificates to contacts failed");
        }
    }

    public void Dispose() => _gate.Dispose();
}
