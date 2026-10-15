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
public sealed record Finding(Severity Severity, string Title, string? Detail = null);
