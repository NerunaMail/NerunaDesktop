using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Mail;

namespace Neruna.Core.Providers;

/// <summary>Well-known provider ids. Third-party providers may register others.</summary>
public static class ProviderIds
{
    public const string Imap = "imap";
    public const string CalDav = "caldav";
    public const string CardDav = "carddav";
    public const string Ics = "ics";
    public const string Ews = "ews";
    public const string Graph = "graph";
    public const string Birthdays = "birthdays";
    public const string Holidays = "holidays";
}

/// <summary>Describes a provider and creates instances bound to one connection.</summary>
public interface IProviderFactory<out TProvider>
{
    string ProviderId { get; }

    /// <summary>Human-readable name for account settings, e.g. "IMAP/SMTP" or "Internet-Kalender (ICS)".</summary>
    string DisplayName { get; }

    TProvider Create(ServiceConnection connection);

    /// <summary>
    /// Derives connection settings from a discovered configuration, or returns null if this provider cannot use it.
    /// Lets account setup stay protocol-neutral: each provider claims what it understands.
    /// </summary>
    IReadOnlyDictionary<string, string>? SettingsFromDiscovery(MailProviderConfig config, string emailAddress) => null;

    /// <summary>
    /// The reverse of <see cref="SettingsFromDiscovery"/>: a connection's settings as servers (host, port, security,
    /// user; URLs), so "Konto bearbeiten" can show them protocol-neutrally. Null if this provider cannot describe them.
    /// </summary>
    MailProviderConfig? DescribeSettings(IReadOnlyDictionary<string, string> settings) => null;

    /// <summary>One line for the account list, e.g. "mail.example.ch:993 · SMTP mail.example.ch:465 · anna" or a URL.</summary>
    string Summary(IReadOnlyDictionary<string, string> settings) => string.Empty;
}

public sealed class ProviderNotFoundException(string providerId, ServiceKind kind)
    : Exception($"No {kind} provider is registered for '{providerId}'.")
{
    public string ProviderId { get; } = providerId;

    public ServiceKind Kind { get; } = kind;
}

/// <summary>
/// Resolves provider factories by id. Adding a protocol means registering one more factory here;
/// controllers, storage and UI stay unchanged.
/// </summary>
public sealed class ProviderRegistry(
    IEnumerable<IProviderFactory<IMailProvider>> mailFactories,
    IEnumerable<IProviderFactory<ICalendarProvider>> calendarFactories,
    IEnumerable<IProviderFactory<IContactProvider>> contactFactories)
{
    private readonly Dictionary<string, IProviderFactory<IMailProvider>> _mail = Index(mailFactories);
    private readonly Dictionary<string, IProviderFactory<ICalendarProvider>> _calendar = Index(calendarFactories);
    private readonly Dictionary<string, IProviderFactory<IContactProvider>> _contacts = Index(contactFactories);

    public IEnumerable<IProviderFactory<IMailProvider>> MailProviders => _mail.Values;

    public IEnumerable<IProviderFactory<ICalendarProvider>> CalendarProviders => _calendar.Values;

    public IEnumerable<IProviderFactory<IContactProvider>> ContactProviders => _contacts.Values;

    /// <summary>Connections whose provider is missing are skipped (see <see cref="CreateMail"/>).</summary>
    public bool HasMailProvider(string providerId) => _mail.ContainsKey(providerId);

    public IMailProvider CreateMail(ServiceConnection connection) =>
        Resolve(_mail, connection, ServiceKind.Mail).Create(connection);

    public ICalendarProvider CreateCalendar(ServiceConnection connection) =>
        Resolve(_calendar, connection, ServiceKind.Calendar).Create(connection);

    public IContactProvider CreateContacts(ServiceConnection connection) =>
        Resolve(_contacts, connection, ServiceKind.Contacts).Create(connection);

    /// <summary>The provider's one-line description of the connection; empty if it has none or is not in this build.</summary>
    public string Summary(ServiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            return connection.Kind switch
            {
                ServiceKind.Mail => _mail.GetValueOrDefault(connection.ProviderId)?.Summary(connection.Settings),
                ServiceKind.Calendar => _calendar.GetValueOrDefault(connection.ProviderId)?.Summary(connection.Settings),
                _ => _contacts.GetValueOrDefault(connection.ProviderId)?.Summary(connection.Settings),
            } ?? string.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException)
        {
            return string.Empty; // incomplete settings: no description rather than a broken list
        }
    }

    private static IProviderFactory<T> Resolve<T>(Dictionary<string, IProviderFactory<T>> factories, ServiceConnection connection, ServiceKind kind)
    {
        if (connection.Kind != kind)
        {
            throw new ArgumentException($"Connection {connection.Id} is a {connection.Kind} connection, not {kind}.", nameof(connection));
        }

        return factories.TryGetValue(connection.ProviderId, out var factory)
            ? factory
            : throw new ProviderNotFoundException(connection.ProviderId, kind);
    }

    private static Dictionary<string, IProviderFactory<T>> Index<T>(IEnumerable<IProviderFactory<T>> factories) =>
        factories.ToDictionary(f => f.ProviderId, StringComparer.OrdinalIgnoreCase);
}
