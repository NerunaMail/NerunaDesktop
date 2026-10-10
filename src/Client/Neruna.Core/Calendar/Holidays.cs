using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Calendar;

/// <summary>A public holiday on one day.</summary>
/// <param name="Name">In the UI language.</param>
/// <param name="DayOff">False: a regional or commemorative day that is usually no general day off (e.g. Austria's patron saints).</param>
public sealed record Holiday(DateOnly Date, string Name, bool DayOff = true);

/// <summary>A country with its regions (cantons, states); a holiday calendar is for one country and region.</summary>
public sealed record HolidayCountry(string Code, string Name, IReadOnlyList<HolidayRegion> Regions);

public sealed record HolidayRegion(string Code, string Name);

/// <summary>
/// Public holidays computed from rules (Easter and fixed dates, "third Sunday" …) – no internet service, works offline
/// for any year. Switzerland (26 cantons), Liechtenstein, Germany (16 states), Austria (9 states), France and Italy.
/// Without guarantee: some days are off only in parts of a canton or by custom.
/// </summary>
public static class Holidays
{
    // One code per holiday rule; the tables below say which apply where.
    private const string NewYear = "NY", Berchtold = "B2", Epiphany = "H3", Joseph = "JOS", GoodFriday = "GF", EasterSunday = "ES",
        EasterMonday = "EM", Labour = "MAY1", Ascension = "ASC", WhitSunday = "WS", WhitMonday = "WM", CorpusChristi = "CC",
        PeterPaul = "PP", Assumption = "AS", AllSaints = "AH", Immaculate = "IC", Christmas = "XM", StStephen = "ST";

    private static readonly Dictionary<string, string[]> Cantons = new()
    {
        ["ZH"] = [NewYear, Berchtold, GoodFriday, EasterMonday, Labour, Ascension, WhitMonday, "CH-NAT", Christmas, StStephen],
        ["BE"] = [NewYear, Berchtold, GoodFriday, EasterMonday, Ascension, WhitMonday, "CH-NAT", Christmas, StStephen],
        ["LU"] = [NewYear, Berchtold, GoodFriday, EasterMonday, Ascension, WhitMonday, CorpusChristi, "CH-NAT", Assumption, AllSaints, Immaculate, Christmas, StStephen],
        ["UR"] = [NewYear, Epiphany, Joseph, GoodFriday, EasterMonday, Ascension, WhitMonday, CorpusChristi, "CH-NAT", Assumption, AllSaints, Immaculate, Christmas, StStephen],
        ["SZ"] = [NewYear, Epiphany, Joseph, GoodFriday, EasterMonday, Ascension, WhitMonday, CorpusChristi, "CH-NAT", Assumption, AllSaints, Immaculate, Christmas, StStephen],
        ["OW"] = [NewYear, Berchtold, GoodFriday, EasterMonday, Ascension, WhitMonday, CorpusChristi, "CH-NAT", Assumption, "CH-BK", AllSaints, Immaculate, Christmas, StStephen],
        ["NW"] = [NewYear, Joseph, GoodFriday, EasterMonday, Ascension, WhitMonday, CorpusChristi, "CH-NAT", Assumption, AllSaints, Immaculate, Christmas, StStephen],
        ["GL"] = [NewYear, Berchtold, "CH-NAF", GoodFriday, EasterMonday, Ascension, WhitMonday, "CH-NAT", AllSaints, Christmas, StStephen],
        ["ZG"] = [NewYear, Berchtold, GoodFriday, EasterMonday, Ascension, WhitMonday, CorpusChristi, "CH-NAT", Assumption, AllSaints, Immaculate, Christmas, StStephen],
        ["FR"] = [NewYear, Berchtold, GoodFriday, EasterMonday, Ascension, WhitMonday, CorpusChristi, "CH-NAT", Assumption, AllSaints, Immaculate, Christmas, StStephen],
        ["SO"] = [NewYear, Berchtold, GoodFriday, EasterMonday, Labour, Ascension, WhitMonday, CorpusChristi, "CH-NAT", Assumption, AllSaints, Immaculate, Christmas, StStephen],
        ["BS"] = [NewYear, GoodFriday, EasterMonday, Labour, Ascension, WhitMonday, "CH-NAT", Christmas, StStephen],
        ["BL"] = [NewYear, GoodFriday, EasterMonday, Labour, Ascension, WhitMonday, "CH-NAT", Christmas, StStephen],
        ["SH"] = [NewYear, Berchtold, GoodFriday, EasterMonday, Labour, Ascension, WhitMonday, "CH-NAT", Christmas, StStephen],
        ["AR"] = [NewYear, GoodFriday, EasterMonday, Ascension, WhitMonday, "CH-NAT", Christmas, StStephen],
        ["AI"] = [NewYear, GoodFriday, EasterMonday, Ascension, WhitMonday, CorpusChristi, "CH-NAT", Assumption, "CH-MAU", AllSaints, Immaculate, Christmas, StStephen],
        ["SG"] = [NewYear, GoodFriday, EasterMonday, Ascension, WhitMonday, "CH-NAT", AllSaints, Christmas, StStephen],
        ["GR"] = [NewYear, GoodFriday, EasterMonday, Ascension, WhitMonday, "CH-NAT", Christmas, StStephen],
        ["AG"] = [NewYear, Berchtold, GoodFriday, EasterMonday, Ascension, WhitMonday, CorpusChristi, "CH-NAT", Assumption, AllSaints, Immaculate, Christmas, StStephen],
        ["TG"] = [NewYear, Berchtold, GoodFriday, EasterMonday, Labour, Ascension, WhitMonday, "CH-NAT", Christmas, StStephen],
        ["TI"] = [NewYear, Epiphany, Joseph, EasterMonday, Labour, Ascension, WhitMonday, CorpusChristi, PeterPaul, "CH-NAT", Assumption, AllSaints, Immaculate, Christmas, StStephen],
        ["VD"] = [NewYear, Berchtold, GoodFriday, EasterMonday, Ascension, WhitMonday, "CH-NAT", "CH-LJ", Christmas],
        ["VS"] = [NewYear, Joseph, EasterMonday, Ascension, WhitMonday, CorpusChristi, "CH-NAT", Assumption, AllSaints, Immaculate, Christmas],
        ["NE"] = [NewYear, "CH-NE", GoodFriday, EasterMonday, Ascension, WhitMonday, "CH-NAT", "CH-LJ", Christmas],
        ["GE"] = [NewYear, GoodFriday, EasterMonday, Ascension, WhitMonday, "CH-NAT", "CH-JG", Christmas, "CH-RES"],
        ["JU"] = [NewYear, Berchtold, GoodFriday, EasterMonday, Labour, Ascension, WhitMonday, CorpusChristi, "CH-JU", "CH-NAT", Assumption, AllSaints, Christmas],
    };

    private static readonly string[] GermanyAll = [NewYear, GoodFriday, EasterMonday, Labour, Ascension, WhitMonday, "DE-UNITY", Christmas, StStephen];

    private static readonly Dictionary<string, string[]> GermanStates = new()
    {
        ["BW"] = [Epiphany, CorpusChristi, AllSaints],
        ["BY"] = [Epiphany, CorpusChristi, Assumption, AllSaints],
        ["BE"] = ["DE-WOMEN"],
        ["BB"] = [EasterSunday, WhitSunday, "DE-REF"],
        ["HB"] = ["DE-REF"],
        ["HH"] = ["DE-REF"],
        ["HE"] = [CorpusChristi],
        ["MV"] = ["DE-WOMEN", "DE-REF"],
        ["NI"] = ["DE-REF"],
        ["NW"] = [CorpusChristi, AllSaints],
        ["RP"] = [CorpusChristi, AllSaints],
        ["SL"] = [CorpusChristi, Assumption, AllSaints],
        ["SN"] = ["DE-REF", "DE-BUSS"],
        ["ST"] = [Epiphany, "DE-REF"],
        ["SH"] = ["DE-REF"],
        ["TH"] = ["DE-CHILD", "DE-REF"],
    };

    private static readonly string[] AustriaAll = [NewYear, Epiphany, EasterMonday, "AT-STATE", Ascension, WhitMonday, CorpusChristi, Assumption, "AT-NAT", AllSaints, Immaculate, Christmas, StStephen];

    private static readonly Dictionary<string, string[]> AustrianStates = new()
    {
        ["B"] = ["AT-MARTIN"],
        ["K"] = ["AT-JOSEPH", "AT-PLEB"],
        ["NO"] = ["AT-LEOPOLD"],
        ["OO"] = ["AT-FLORIAN"],
        ["S"] = ["AT-RUPERT"],
        ["ST"] = ["AT-JOSEPH"],
        ["T"] = ["AT-JOSEPH"],
        ["V"] = ["AT-JOSEPH"],
        ["W"] = ["AT-LEOPOLD"],
    };

    private static readonly string[] Liechtenstein = [NewYear, Berchtold, Epiphany, "LI-CANDLEMAS", Joseph, EasterMonday, Labour, Ascension, WhitMonday, CorpusChristi, "LI-STATE", "LI-NATIVITY", AllSaints, Immaculate, "LI-EVE", Christmas, StStephen, "LI-SILVESTER"];

    private static readonly string[] France = [NewYear, EasterMonday, Labour, "FR-VICTORY", Ascension, WhitMonday, "FR-NAT", Assumption, AllSaints, "FR-ARMISTICE", Christmas];

    private static readonly string[] Italy = [NewYear, Epiphany, EasterSunday, EasterMonday, "IT-LIBERATION", Labour, "IT-REPUBLIC", Assumption, "IT-FRANCIS", AllSaints, Immaculate, Christmas, StStephen];

    /// <summary>The countries and their regions, names in the UI language.</summary>
    public static IReadOnlyList<HolidayCountry> Countries =>
    [
        new("CH", T("Schweiz"), Regions(
            ("AG", T("Aargau")), ("AI", T("Appenzell Innerrhoden")), ("AR", T("Appenzell Ausserrhoden")), ("BE", T("Bern")),
            ("BL", T("Basel-Landschaft")), ("BS", T("Basel-Stadt")), ("FR", T("Freiburg")), ("GE", T("Genf")), ("GL", T("Glarus")),
            ("GR", T("Graubünden")), ("JU", T("Jura")), ("LU", T("Luzern")), ("NE", T("Neuenburg")), ("NW", T("Nidwalden")),
            ("OW", T("Obwalden")), ("SG", T("St. Gallen")), ("SH", T("Schaffhausen")), ("SO", T("Solothurn")), ("SZ", T("Schwyz")),
            ("TG", T("Thurgau")), ("TI", T("Tessin")), ("UR", T("Uri")), ("VD", T("Waadt")), ("VS", T("Wallis")), ("ZG", T("Zug")),
            ("ZH", T("Zürich")))),
        new("LI", T("Liechtenstein"), []),
        new("DE", T("Deutschland"), Regions(
            ("BW", T("Baden-Württemberg")), ("BY", T("Bayern")), ("BE", T("Berlin")), ("BB", T("Brandenburg")), ("HB", T("Bremen")),
            ("HH", T("Hamburg")), ("HE", T("Hessen")), ("MV", T("Mecklenburg-Vorpommern")), ("NI", T("Niedersachsen")),
            ("NW", T("Nordrhein-Westfalen")), ("RP", T("Rheinland-Pfalz")), ("SL", T("Saarland")), ("SN", T("Sachsen")),
            ("ST", T("Sachsen-Anhalt")), ("SH", T("Schleswig-Holstein")), ("TH", T("Thüringen")))),
        new("AT", T("Österreich"), Regions(
            ("B", T("Burgenland")), ("K", T("Kärnten")), ("NO", T("Niederösterreich")), ("OO", T("Oberösterreich")),
            ("S", T("Salzburg")), ("ST", T("Steiermark")), ("T", T("Tirol")), ("V", T("Vorarlberg")), ("W", T("Wien")))),
        new("FR", T("Frankreich"), []),
        new("IT", T("Italien"), []),
    ];

    /// <summary>"Schweiz – Zürich", "Frankreich".</summary>
    public static string DisplayName(string country, string? region)
    {
        var c = Countries.FirstOrDefault(c => c.Code == country);
        var r = c?.Regions.FirstOrDefault(r => r.Code == region);
        return c is null ? country : r is null ? c.Name : $"{c.Name} – {r.Name}";
    }

    /// <summary>The holidays of one year, by date.</summary>
    /// <exception cref="ArgumentException">Unknown country, or a region is needed (Switzerland, Germany, Austria).</exception>
    public static IReadOnlyList<Holiday> For(string country, string? region, int year)
    {
        ArgumentNullException.ThrowIfNull(country);
        IEnumerable<string> rules = country switch
        {
            "CH" => Cantons.GetValueOrDefault(region ?? string.Empty) ?? throw new ArgumentException($"Unknown canton {region}.", nameof(region)),
            "DE" => GermanyAll.Concat(GermanStates.GetValueOrDefault(region ?? string.Empty) ?? throw new ArgumentException($"Unknown state {region}.", nameof(region))),
            "AT" => AustriaAll.Concat(AustrianStates.GetValueOrDefault(region ?? string.Empty) ?? throw new ArgumentException($"Unknown state {region}.", nameof(region))),
            "LI" => Liechtenstein,
            "FR" => France,
            "IT" => Italy,
            _ => throw new ArgumentException($"Unknown country {country}.", nameof(country)),
        };

        var easter = Easter(year);
        return rules.Select(rule => Resolve(rule, country, year, easter)).OfType<Holiday>().OrderBy(h => h.Date).ToList();
    }

    /// <summary>Western (Gregorian) Easter Sunday – anonymous Gregorian algorithm.</summary>
    public static DateOnly Easter(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = ((19 * a) + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        var m = (a + (11 * h) + (22 * l)) / 451;
        var month = (h + l - (7 * m) + 114) / 31;
        var day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    private static Holiday? Resolve(string rule, string country, int year, DateOnly easter)
    {
        DateOnly On(int month, int day) => new(year, month, day);
        return rule switch
        {
            NewYear => new(On(1, 1), T("Neujahr")),
            Berchtold => new(On(1, 2), T("Berchtoldstag")),
            Epiphany => new(On(1, 6), T("Heilige Drei Könige")),
            Joseph => new(On(3, 19), T("Josefstag")),
            GoodFriday => new(easter.AddDays(-2), T("Karfreitag")),
            EasterSunday => new(easter, T("Ostersonntag")),
            EasterMonday => new(easter.AddDays(1), T("Ostermontag")),
            Labour => new(On(5, 1), T("Tag der Arbeit")),
            Ascension => new(easter.AddDays(39), country == "CH" ? T("Auffahrt") : T("Christi Himmelfahrt")),
            WhitSunday => new(easter.AddDays(49), T("Pfingstsonntag")),
            WhitMonday => new(easter.AddDays(50), T("Pfingstmontag")),
            CorpusChristi => new(easter.AddDays(60), T("Fronleichnam")),
            PeterPaul => new(On(6, 29), T("Peter und Paul")),
            Assumption => new(On(8, 15), T("Mariä Himmelfahrt")),
            AllSaints => new(On(11, 1), T("Allerheiligen")),
            Immaculate => new(On(12, 8), T("Mariä Empfängnis")),
            Christmas => new(On(12, 25), country == "DE" ? T("1. Weihnachtstag") : T("Weihnachten")),
            StStephen => new(On(12, 26), country == "DE" ? T("2. Weihnachtstag") : T("Stephanstag")),

            "CH-NAT" => new(On(8, 1), T("Bundesfeiertag")),
            "CH-NAF" => new(Nth(year, 4, DayOfWeek.Thursday, 1), T("Näfelser Fahrt")),
            "CH-BK" => new(On(9, 25), T("Bruder-Klausen-Fest")),
            "CH-MAU" => new(On(9, 22), T("Mauritiustag")),
            "CH-LJ" => new(Nth(year, 9, DayOfWeek.Sunday, 3).AddDays(1), T("Bettagsmontag")),
            "CH-JG" => new(Nth(year, 9, DayOfWeek.Sunday, 1).AddDays(4), T("Genfer Bettag")),
            "CH-NE" => new(On(3, 1), T("Ausrufung der Republik Neuenburg")),
            "CH-RES" => new(On(12, 31), T("Wiederherstellung der Republik Genf")),
            "CH-JU" => new(On(6, 23), T("Fest der Unabhängigkeit des Juras")),

            "DE-UNITY" => new(On(10, 3), T("Tag der Deutschen Einheit")),
            "DE-WOMEN" => new(On(3, 8), T("Internationaler Frauentag")),
            "DE-REF" => new(On(10, 31), T("Reformationstag")),
            "DE-CHILD" => year >= 2019 ? new(On(9, 20), T("Weltkindertag")) : null,
            // Wednesday before 23 November.
            "DE-BUSS" => new(LastBefore(On(11, 23), DayOfWeek.Wednesday), T("Buss- und Bettag")),

            "AT-STATE" => new(On(5, 1), T("Staatsfeiertag")),
            "AT-NAT" => new(On(10, 26), T("Nationalfeiertag")),
            "AT-JOSEPH" => new(On(3, 19), T("Hl. Josef (Landespatron)"), DayOff: false),
            "AT-FLORIAN" => new(On(5, 4), T("Hl. Florian (Landespatron)"), DayOff: false),
            "AT-RUPERT" => new(On(9, 24), T("Hl. Rupert (Landespatron)"), DayOff: false),
            "AT-PLEB" => new(On(10, 10), T("Tag der Volksabstimmung"), DayOff: false),
            "AT-MARTIN" => new(On(11, 11), T("Hl. Martin (Landespatron)"), DayOff: false),
            "AT-LEOPOLD" => new(On(11, 15), T("Hl. Leopold (Landespatron)"), DayOff: false),

            "LI-CANDLEMAS" => new(On(2, 2), T("Mariä Lichtmess")),
            "LI-STATE" => new(On(8, 15), T("Staatsfeiertag")),
            "LI-NATIVITY" => new(On(9, 8), T("Mariä Geburt")),
            "LI-EVE" => new(On(12, 24), T("Heiligabend")),
            "LI-SILVESTER" => new(On(12, 31), T("Silvester")),

            "FR-VICTORY" => new(On(5, 8), T("Tag des Sieges 1945")),
            "FR-NAT" => new(On(7, 14), T("Nationalfeiertag")),
            "FR-ARMISTICE" => new(On(11, 11), T("Waffenstillstand 1918")),

            "IT-LIBERATION" => new(On(4, 25), T("Tag der Befreiung")),
            "IT-REPUBLIC" => new(On(6, 2), T("Tag der Republik")),
            // Again a national holiday from 2026.
            "IT-FRANCIS" => year >= 2026 ? new(On(10, 4), T("Hl. Franz von Assisi")) : null,
            _ => throw new InvalidOperationException($"Unknown holiday rule {rule}."),
        };
    }

    private static List<HolidayRegion> Regions(params (string Code, string Name)[] regions) =>
        regions.Select(r => new HolidayRegion(r.Code, r.Name)).OrderBy(r => r.Name, StringComparer.Create(Culture, ignoreCase: true)).ToList();

    // The n-th weekday of a month (n from 1).
    private static DateOnly Nth(int year, int month, DayOfWeek day, int n)
    {
        var first = new DateOnly(year, month, 1);
        return first.AddDays((((int)day - (int)first.DayOfWeek + 7) % 7) + (7 * (n - 1)));
    }

    private static DateOnly LastBefore(DateOnly date, DayOfWeek day)
    {
        var candidate = date.AddDays(-1);
        while (candidate.DayOfWeek != day)
        {
            candidate = candidate.AddDays(-1);
        }

        return candidate;
    }
}
