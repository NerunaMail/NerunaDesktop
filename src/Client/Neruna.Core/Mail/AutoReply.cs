namespace Neruna.Core.Mail;

/// <summary>
/// The out-of-office reply of a mail account ("Abwesenheitsnotiz"): kept on the server, which answers incoming mail
/// while Neruna is closed. Plain text; the provider turns it into what its server expects.
/// </summary>
/// <param name="Start">With <paramref name="End"/>: only answered in this period (dates, the provider rounds to days if it must).</param>
/// <param name="Subject">Subject of the reply; null for the server's default ("Abwesend: …").</param>
public sealed record AutoReply(bool IsEnabled, string Message, DateTimeOffset? Start = null, DateTimeOffset? End = null, string? Subject = null)
{
    public static AutoReply Off { get; } = new(false, string.Empty);

    public bool IsScheduled => Start is not null && End is not null;

    /// <summary>Switched on and – if scheduled – the period has not ended yet.</summary>
    public bool IsActive(DateTimeOffset now) => IsEnabled && (!IsScheduled || End > now);
}

/// <summary>What the account's server can do beyond on/off and a message.</summary>
[Flags]
public enum AutoReplyFeatures
{
    None = 0,

    /// <summary>Answers only in a period (Graph; Sieve with the "date" extension).</summary>
    Schedule = 1,

    /// <summary>An own subject for the reply (Sieve).</summary>
    Subject = 2,
}

/// <summary>A mail provider whose server can answer while the user is away (Graph, IMAP via ManageSieve).</summary>
public interface IAutoReplyProvider
{
    /// <exception cref="AutoReplyUnavailableException">This account's server offers none (or a permission is missing).</exception>
    Task<(AutoReply Reply, AutoReplyFeatures Features)> GetAutoReplyAsync(CancellationToken cancellationToken = default);

    /// <param name="ownAddresses">The account's addresses (with aliases): mail to any of them is answered.</param>
    /// <exception cref="AutoReplyUnavailableException">This account's server offers none (or a permission is missing).</exception>
    Task SetAutoReplyAsync(AutoReply reply, IReadOnlyList<string> ownAddresses, CancellationToken cancellationToken = default);
}

/// <summary>The account cannot have an out-of-office reply in Neruna – the message says why (for the user).</summary>
public sealed class AutoReplyUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
