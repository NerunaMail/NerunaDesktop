using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Ai;

/// <summary>
/// The instructions for Neruna's AI tasks, in one place. Written in English (models follow it most reliably); the
/// answer comes in the language of the user interface.
/// </summary>
public enum AiRewrite
{
    Improve,
    Shorter,
    Formal,
    Friendly,

    /// <summary>As the user's own instructions say (e.g. "auf Englisch übersetzen").</summary>
    Custom,
}

public static class AiTasks
{
    /// <summary>Longer material is cut: enough for any normal mail, and the request stays affordable.</summary>
    public const int MaxInput = 40_000;

    public static AiRequest Summarize(string? subject, string? from, string text) => new(
        "You summarise an e-mail for its recipient. Give the gist in two to four short sentences, then – only if there are any – "
        + "the requests, questions or deadlines addressed to the recipient as a short list. Plain text, no greeting, no headings. "
        + "Do not invent anything that is not in the mail. " + AnswerIn(),
        [new AiMessage("user", $"Subject: {subject}\nFrom: {from}\n\n{Cut(text)}")]);

    private const string PlainText = "Plain text only: paragraphs separated by an empty line, no Markdown, no comments about what you did.";

    /// <summary>
    /// The text of a new mail or a reply, from the user's instructions (and what they wrote so far). For a reply the quoted
    /// message is the material; it is answered in its own language.
    /// </summary>
    public static AiRequest Draft(string? instructions, string? subject, string? to, string? from, string ownText, string quote)
    {
        var reply = !string.IsNullOrWhiteSpace(quote);
        return new AiRequest(
            "You write the body of an e-mail for the user. "
            + (reply
                ? "It answers (or forwards) the quoted message: respond to what it says, as the user's instructions ask. Write in the language of the quoted message unless the instructions say otherwise. "
                : "Write in the language of the user's instructions. ")
            + "If the user already wrote something, build on it instead of starting over. Begin with a fitting greeting and end with a short closing phrase, "
            + "but without name, title or contact details – the mail has a signature. No subject line. No placeholders in brackets unless a fact is truly unknown. "
            + "Keep names, numbers and dates exactly as given. " + Swiss() + PlainText,
            [new AiMessage("user", $"Instructions: {(string.IsNullOrWhiteSpace(instructions) ? "(none – write a fitting answer)" : instructions)}\n"
                                   + $"Subject: {subject}\nTo: {to}\nFrom (the user): {from}\n\n"
                                   + $"Written so far:\n{Cut(ownText)}\n\n"
                                   + (reply ? $"Quoted message:\n{Cut(quote)}" : string.Empty))]);
    }

    /// <summary>A passage of the user's mail, rewritten: improved, shorter, more formal or friendlier, or as instructed.</summary>
    public static AiRequest Rewrite(string text, AiRewrite how, string? instructions = null) => new(
        "You rewrite a passage of an e-mail the user is writing. "
        + how switch
        {
            AiRewrite.Shorter => "Make it clearly shorter and more direct. ",
            AiRewrite.Formal => "Make it more formal and polite, suitable for business. ",
            AiRewrite.Friendly => "Make it warmer and friendlier, still professional. ",
            AiRewrite.Custom => $"Apply this change: {instructions} ",
            _ => "Correct spelling, grammar and punctuation and improve the wording where it is clumsy; change as little as needed. ",
        }
        + "Keep the meaning and keep names, numbers and dates exactly. Keep the language of the passage unless the change asks for a translation. "
        + Swiss() + "Return only the rewritten passage. " + PlainText,
        [new AiMessage("user", Cut(text))]);

    // Swiss users write German without "ß".
    private static string Swiss() => Language == "de" ? "When you write German, write \"ss\", never \"ß\". " : string.Empty;

    /// <summary>The language of the answers: the one of the user interface.</summary>
    public static string AnswerIn() => Language switch
    {
        "en" => "Answer in English.",
        "fr" => "Answer in French.",
        "it" => "Answer in Italian.",
        _ => "Answer in Swiss Standard German (write \"ss\", never \"ß\").",
    };

    private static string Cut(string text) => text.Length <= MaxInput ? text : text[..MaxInput] + "\n[…]";
}
