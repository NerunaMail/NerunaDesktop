using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Mail;

namespace Neruna.Core;

/// <summary>
/// The one way to sync everything (mail, calendar, contacts). Never two runs at once – calendar and contacts have no
/// locks of their own. A request while a run is going waits for the next run; all requests waiting meanwhile share
/// that one run, so a busy timer, the sync button and a new account do not pile up full syncs.
/// </summary>
public sealed class SyncCoordinator(MailController mail, CalendarController calendar, ContactController contacts) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
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
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
