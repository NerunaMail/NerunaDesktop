using Neruna.Core;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Providers.Special;

namespace Neruna.Client.Tests;

/// <summary>Calendars Neruna computes itself: birthdays from the contacts and public holidays.</summary>
public class SpecialCalendarTests
{
    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2038, 4, 25)]
    public void Easter_is_computed(int year, int month, int day) => Assert.Equal(new DateOnly(year, month, day), Holidays.Easter(year));

    [Fact]
    public void Holidays_follow_country_and_region()
    {
        static string[] Days(string country, string? region) => [.. Holidays.For(country, region, 2026).Select(h => h.Date.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture))];

        Assert.Equal(["01-01", "01-02", "04-03", "04-06", "05-01", "05-14", "05-25", "08-01", "12-25", "12-26"], Days("CH", "ZH"));
        Assert.Contains("09-10", Days("CH", "GE"));  // Jeûne genevois: Thursday after the first Sunday of September
        Assert.Contains("12-31", Days("CH", "GE"));
        Assert.Contains("09-21", Days("CH", "VD"));  // Monday after the third Sunday of September
        Assert.Contains("04-02", Days("CH", "GL"));  // Näfelser Fahrt: first Thursday of April
        Assert.Contains("06-04", Days("CH", "LU"));  // Corpus Christi
        Assert.Contains("11-18", Days("DE", "SN"));  // Buss- und Bettag: Wednesday before 23 November
        Assert.DoesNotContain("11-18", Days("DE", "BY"));
        Assert.Contains("10-03", Days("DE", "BY"));
        Assert.Contains("10-04", Days("IT", null));  // San Francesco again from 2026
        Assert.DoesNotContain(Holidays.For("IT", null, 2025), h => h.Date.Month == 10 && h.Date.Day == 4);
        Assert.Contains(Holidays.For("AT", "W", 2026), h => h.Date == new DateOnly(2026, 11, 15) && !h.DayOff);
        Assert.Equal(11, Days("FR", null).Length);
        Assert.Throws<ArgumentException>(() => Holidays.For("CH", null, 2026));

        Assert.Equal(26, Holidays.Countries.Single(c => c.Code == "CH").Regions.Count);
        Assert.Equal(16, Holidays.Countries.Single(c => c.Code == "DE").Regions.Count);
    }

    [Theory]
    [InlineData("1985-04-15", 1985, 4, 15)]
    [InlineData("19850415", 1985, 4, 15)]
    [InlineData("1985-04-15T00:00:00Z", 1985, 4, 15)]
    [InlineData("--0415", null, 4, 15)]
    [InlineData("--04-15", null, 4, 15)]
    [InlineData("1604-04-15", null, 4, 15)]
    public void Birthdays_are_read_in_all_vcard_forms(string value, int? year, int month, int day) =>
        Assert.Equal(new ContactBirthday(year, month, day), ContactBirthday.Parse(value));

    [Fact]
    public void Birthdays_are_typed_and_written()
    {
        Assert.Equal(new ContactBirthday(1985, 4, 15), ContactBirthday.ParseInput("15.4.1985"));
        Assert.Equal(new ContactBirthday(null, 2, 29), ContactBirthday.ParseInput("29.02."));
        Assert.Null(ContactBirthday.ParseInput("31.04.1985"));
        Assert.Null(ContactBirthday.ParseInput("29.02.2023"));

        var vcard = new ContactDraft("Anna", "Muster", null, null, [], [], null) { Birthday = new ContactBirthday(1985, 4, 15) }.ToVCard(null);
        Assert.Contains("BDAY:1985-04-15", vcard, StringComparison.Ordinal);
        Assert.Equal(new ContactBirthday(1985, 4, 15), VCardReader.Read(vcard).Birthday);

        // Editing other fields keeps the birthday; removing it removes the line.
        var edited = ContactDraft.FromVCard(vcard) with { Note = "neu" };
        Assert.Contains("BDAY:1985-04-15", edited.ToVCard(vcard), StringComparison.Ordinal);
        Assert.DoesNotContain("BDAY", (edited with { Birthday = null }).ToVCard(vcard), StringComparison.Ordinal);
        Assert.Equal(new DateOnly(2027, 2, 28), new ContactBirthday(2000, 2, 29).In(2027));
    }

    [Fact]
    public async Task Birthday_calendar_shows_age_reminds_and_follows_the_contacts()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new Contacts();
        store.Add("Anna Muster", "BDAY:1985-04-15");
        store.Add("Beat Beispiel", "BDAY:--12-24");
        store.Add("Anna Muster", "BDAY:1985-04-15"); // same person in a second address book
        store.Add("Ohne Datum", null);
        var time = new FixedTime(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var provider = new BirthdayCalendarProvider(Guid.NewGuid(), store, BirthdayReminder.DayBefore, time);
        var calendar = Assert.Single(await provider.GetCalendarsAsync(ct));
        Assert.True(calendar.IsReadOnly);

        var result = await provider.SyncCalendarAsync(calendar, new Dictionary<string, string?>(), ct);
        Assert.Equal(8, result.AddedOrChanged.Count); // 2 people × 2025–2028
        var anna2026 = Assert.Single(result.AddedOrChanged, o => o.ICalendarData.Contains("SUMMARY:Geburtstag Anna Muster (41)\r\n", StringComparison.Ordinal));
        Assert.Contains("DTSTART;VALUE=DATE:20260415", anna2026.ICalendarData, StringComparison.Ordinal);
        Assert.Contains("TRIGGER:-PT15H", anna2026.ICalendarData, StringComparison.Ordinal);
        Assert.Contains("TRANSP:TRANSPARENT", anna2026.ICalendarData, StringComparison.Ordinal);
        Assert.Contains(result.AddedOrChanged, o => o.ICalendarData.Contains("SUMMARY:Geburtstag Beat Beispiel\r\n", StringComparison.Ordinal));

        // Nothing changed: nothing to store. A new birthday: everything again.
        var unchanged = await provider.SyncCalendarAsync(calendar with { SyncState = result.NewSyncState }, new Dictionary<string, string?>(), ct);
        Assert.Empty(unchanged.AddedOrChanged);
        store.Add("Carla Neu", "BDAY:2000-01-01");
        var changed = await provider.SyncCalendarAsync(calendar with { SyncState = result.NewSyncState }, new Dictionary<string, string?>(), ct);
        Assert.True(changed.IsFullResync);
        Assert.Equal(12, changed.AddedOrChanged.Count);
    }

    [Fact]
    public async Task Holiday_calendar_lists_the_years_around_today()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new HolidayCalendarProvider(Guid.NewGuid(), "CH", "ZH", new FixedTime(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero)));
        var calendar = Assert.Single(await provider.GetCalendarsAsync(ct));
        Assert.Equal("Feiertage Schweiz – Zürich", calendar.Name);
        var result = await provider.SyncCalendarAsync(calendar, new Dictionary<string, string?>(), ct);
        Assert.Equal(40, result.AddedOrChanged.Count);
        Assert.Contains(result.AddedOrChanged, o => o.ICalendarData.Contains("DTSTART;VALUE=DATE:20260801", StringComparison.Ordinal)
                                                   && o.ICalendarData.Contains("SUMMARY:Bundesfeiertag", StringComparison.Ordinal));
        Assert.Equal(result.AddedOrChanged.Count, result.AddedOrChanged.Select(o => o.RemoteId).Distinct().Count());

        await Assert.ThrowsAsync<ArgumentException>(() => new HolidayCalendarProvider(Guid.NewGuid(), "XX", null, TimeProvider.System).TestConnectionAsync(ct));
    }

    private sealed class Contacts : IContactStore
    {
        private readonly List<(AddressBookInfo Book, ContactObject Contact)> _contacts = [];

        public void Add(string name, string? birthday)
        {
            var book = new AddressBookInfo(Guid.NewGuid(), "book", "Kontakte", IsReadOnly: false);
            var vcard = $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{Guid.NewGuid()}\r\nFN:{name}\r\n{(birthday is null ? string.Empty : birthday + "\r\n")}END:VCARD\r\n";
            _contacts.Add((book, new ContactObject(Guid.NewGuid().ToString(), null, vcard)));
        }

        public Task<IReadOnlyList<AddressBookInfo>> GetAddressBooksAsync(Guid? connectionId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AddressBookInfo>>([.. _contacts.Select(c => c.Book)]);

        public Task<IReadOnlyList<ContactObject>> GetContactsAsync(Guid connectionId, string addressBookRemoteId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContactObject>>([.. _contacts.Where(c => c.Book.ConnectionId == connectionId).Select(c => c.Contact)]);

        public Task<IReadOnlyList<AddressBookInfo>> MergeAddressBooksAsync(Guid connectionId, IReadOnlyList<AddressBookInfo> remoteAddressBooks, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string?>> GetContactVersionsAsync(Guid connectionId, string addressBookRemoteId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ApplySyncResultAsync(AddressBookInfo addressBook, AddressBookSyncResult result, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpsertContactAsync(AddressBookInfo addressBook, ContactObject contact, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteContactAsync(AddressBookInfo addressBook, string remoteId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
