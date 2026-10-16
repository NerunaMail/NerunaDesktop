namespace Neruna.Core.Calendar;

/// <summary>A time someone has something on, as their server tells it (subject and place only if they share them).</summary>
public sealed record BusySlot(DateTimeOffset Start, DateTimeOffset End, ShowAs ShowAs, string? Subject = null, string? Location = null);

/// <summary>What the server knows of one person's time; <paramref name="Error"/> when it could not tell (unknown address, no access).</summary>
public sealed record Availability(string Email, IReadOnlyList<BusySlot> Slots, string? Error = null)
{
    public bool IsKnown => Error is null;
}

/// <summary>
/// Optional: the server answers who is busy when, also for people whose calendar is not subscribed (Microsoft 365:
/// getSchedule, Exchange: GetUserAvailability) – for the planning view and the conflict hint while inviting.
/// </summary>
public interface ICalendarFreeBusy
{
    /// <returns>One entry per address, in the order asked.</returns>
    Task<IReadOnlyList<Availability>> GetFreeBusyAsync(IReadOnlyList<string> emails, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken = default);
}
