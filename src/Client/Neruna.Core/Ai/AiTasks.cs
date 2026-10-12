using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Ai;

/// <summary>
/// The instructions for Neruna's AI tasks, in one place. Written in English (models follow it most reliably); the
/// answer comes in the language of the user interface.
/// </summary>
public static class AiTasks
{
    /// <summary>Longer material is cut: enough for any normal mail, and the request stays affordable.</summary>
    public const int MaxInput = 40_000;

    public static AiRequest Summarize(string? subject, string? from, string text) => new(
        "You summarise an e-mail for its recipient. Give the gist in two to four short sentences, then – only if there are any – "
        + "the requests, questions or deadlines addressed to the recipient as a short list. Plain text, no greeting, no headings. "
        + "Do not invent anything that is not in the mail. " + AnswerIn(),
        [new AiMessage("user", $"Subject: {subject}\nFrom: {from}\n\n{Cut(text)}")]);

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
