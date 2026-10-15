namespace Neruna.Core.Mail;

/// <summary>What a rule looks at: the sender, the recipients (To/Cc), the subject or the text.</summary>
public enum RuleField
{
    From,
    Recipient,
    Subject,
    Body,
}

/// <summary>The field contains <paramref name="Value"/> (case does not matter).</summary>
public sealed record RuleCondition(RuleField Field, string Value);

/// <summary>
/// A rule the mail server applies to incoming mail, also while Neruna is closed ("Serverregeln"). All conditions must
/// apply; then every chosen action is done.
/// </summary>
/// <param name="Id">The server's id; null for a rule not saved yet.</param>
/// <param name="MoveTo">A folder of the account (its remote id) the mail goes to.</param>
/// <param name="Delete">Into the deleted items (wins over <paramref name="MoveTo"/>).</param>
/// <param name="Stop">No further rules for this mail.</param>
public sealed record ServerRule(
    string? Id,
    string Name,
    bool IsEnabled,
    IReadOnlyList<RuleCondition> Conditions,
    string? MoveTo = null,
    bool MarkRead = false,
    bool Flag = false,
    string? ForwardTo = null,
    bool Delete = false,
    bool Stop = false)
{
    public bool HasAction => MoveTo is not null || MarkRead || Flag || !string.IsNullOrWhiteSpace(ForwardTo) || Delete;
}

/// <summary>What the account's server can do beyond sender, recipients, subject, moving and marking read.</summary>
[Flags]
public enum ServerRuleFeatures
{
    None = 0,
    Body = 1,
    Flag = 2,
    Forward = 4,
    Delete = 8,
    Stop = 16,
}

/// <param name="Rules">The rules Neruna can show and change, in their order.</param>
/// <param name="Others">Rules of the account Neruna cannot show completely (made elsewhere): named only, kept as they are.</param>
public sealed record ServerRuleSet(IReadOnlyList<ServerRule> Rules, ServerRuleFeatures Features, IReadOnlyList<string> Others);

/// <summary>A mail provider whose server keeps rules for incoming mail (Sieve, Exchange, Microsoft 365).</summary>
public interface IServerRulesProvider
{
    /// <exception cref="ServerRulesUnavailableException">This account's server offers none (or a permission is missing).</exception>
    Task<ServerRuleSet> GetRulesAsync(CancellationToken cancellationToken = default);

    /// <summary>Makes the server's rules (those Neruna shows) exactly <paramref name="rules"/>, in this order; others stay.</summary>
    /// <exception cref="ServerRulesUnavailableException">Refused by the server (the message says why).</exception>
    Task SaveRulesAsync(IReadOnlyList<ServerRule> rules, CancellationToken cancellationToken = default);
}

/// <summary>Server rules are not possible for the account, or the server refused them – the message says why.</summary>
public sealed class ServerRulesUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
