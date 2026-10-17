namespace Neruna.Core.Privacy;

/// <summary>How much a finding speaks against a mail or link (<see cref="Good"/>: speaks for it).</summary>
public enum Severity
{
    Good,
    Info,
    Low,
    Medium,
    High,
}

/// <summary>One thing the link check or the mail analysis noticed, in words for the user.</summary>
public sealed record Finding(Severity Severity, string Title, string? Detail = null)
{
    /// <summary>What kind of finding it is, for counting (<see cref="FindingCodes"/>); null for the others.</summary>
    public string? Code { get; init; }
}

/// <summary>The kinds of findings Einstellungen → Datenschutz counts.</summary>
public static class FindingCodes
{
    /// <summary>A link to, or a sender of, a known phishing site (the list from neruna.cloud).</summary>
    public const string KnownPhishing = "known-phishing";

    /// <summary>A sender pretending to be someone else: a brand, a contact, the own address, a look-alike domain.</summary>
    public const string Impersonation = "impersonation";
}
