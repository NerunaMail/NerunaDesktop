namespace Neruna.Core.Contacts;

/// <summary>
/// A postal address of a contact (vCard ADR: post office box; extended; street; locality; region; postal code; country).
/// </summary>
/// <param name="Kind">vCard TYPE, e.g. "work" or "home"; null when none.</param>
public sealed record ContactAddress(string? Street, string? PostalCode, string? City, string? Country, string? Kind = null)
{
    public string? Region { get; init; }

    public string? PoBox { get; init; }

    public string? Extended { get; init; }

    public bool IsEmpty => new[] { Street, PostalCode, City, Country, Region, PoBox, Extended }.All(string.IsNullOrWhiteSpace);

    /// <summary>As written on an envelope: street, postal code and city, country – one per line.</summary>
    public string Text => string.Join('\n', new[]
    {
        PoBox, Extended, Street, $"{PostalCode} {City}".Trim(), Region, Country,
    }.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line!.Trim()));

    /// <summary>The same place: street, postal code and city alike (ignoring case, spaces and the kind).</summary>
    public bool SameAs(ContactAddress other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Normal(Street) == Normal(other.Street) && Normal(PostalCode) == Normal(other.PostalCode) && Normal(City) == Normal(other.City);
    }

    private static string Normal(string? value) =>
        string.Concat((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c is not ('.' or ','))).ToUpperInvariant();
}
