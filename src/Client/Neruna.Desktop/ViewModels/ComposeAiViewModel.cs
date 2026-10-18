using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Ai;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// The AI bar while writing: "Schreiben" writes the mail (or the reply to the quoted one) from short instructions;
/// "Verbessern", "Kürzer" … rewrite the marked text, or without a selection the whole own text. The result replaces
/// the text in the editor as an edit – Ctrl+Z brings the previous text back.
/// </summary>
internal sealed partial class ComposeAiViewModel(AiController ai, ComposeViewModel compose) : ViewModelBase
{
    private Action? _cancel;

    /// <summary>An AI provider is connected: the button shows.</summary>
    public bool Available => ai.IsConnected;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Instruction { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(RewriteCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? Notice { get; set; }

    /// <summary>The question before the whole mail goes to the AI (null: none open).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    public partial string? ConfirmText { get; set; }

    public bool IsConfirming => ConfirmText is not null;

    private TaskCompletionSource<bool>? _confirm;

    [RelayCommand]
    private void ConfirmSend() => Answer(true);

    [RelayCommand]
    private void DeclineSend() => Answer(false);

    private void Answer(bool send)
    {
        ConfirmText = null;
        _confirm?.TrySetResult(send);
    }

    // Unless turned off under Einstellungen → KI: the user says "OK" before the whole mail leaves for the provider.
    private async Task<bool> MaySendWholeMailAsync(string question, CancellationToken cancel)
    {
        if (!ai.ConfirmWholeMail)
        {
            return true;
        }

        _confirm = new TaskCompletionSource<bool>();
        ConfirmText = question;
        using (cancel.Register(() => Answer(false)))
        {
            return await _confirm.Task;
        }
    }

    private string ProviderName => ai.ProviderName ?? T("KI");
    public string Placeholder => compose.Kind is ComposeKind.Reply
        ? T("Was soll die Antwort sagen? z. B. «zusagen und Dienstag 14 Uhr vorschlagen» – oder leer lassen")
        : T("Was soll die KI schreiben oder ändern? z. B. «Einladung zum Teamessen am Freitag» oder «auf Englisch übersetzen»");

    [RelayCommand]
    private void Toggle()
    {
        IsOpen = !IsOpen;
        Error = Notice = null;
    }

    [RelayCommand]
    private void Cancel() => _cancel?.Invoke();

    /// <summary>"Schreiben": the mail text from the instructions (a reply answers the quoted message).</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task WriteAsync() => RunAsync(async (editor, cancel) =>
    {
        var texts = await editor.GetTextsAsync();
        if (string.IsNullOrWhiteSpace(Instruction) && string.IsNullOrWhiteSpace(texts.Quote) && string.IsNullOrWhiteSpace(texts.Own))
        {
            Error = T("Bitte kurz beschreiben, was die Mail sagen soll.");
            return;
        }

        if ((!string.IsNullOrWhiteSpace(texts.Own) || !string.IsNullOrWhiteSpace(texts.Quote))
            && !await MaySendWholeMailAsync(string.IsNullOrWhiteSpace(texts.Quote)
                ? F("Für «Schreiben» sendet Neruna Ihren ganzen bisherigen Text an {0}.", ProviderName)
                : F("Für «Schreiben» sendet Neruna die ganze E-Mail an {0} – Ihren Text und die zitierte Nachricht.", ProviderName), cancel))
        {
            return;
        }

        var text = await CollectAsync(ai.DraftAsync(Instruction, compose.Subject, compose.To, string.IsNullOrWhiteSpace(compose.From.DisplayName) ? compose.From.Email : compose.From.DisplayName, texts.Own, texts.Quote, cancel));
        // An empty line stays between the text and the signature below it.
        if (await editor.ReplaceTextAsync(ToHtml(text) + "<div><br></div>", text + "\n\n", selectionOnly: false))
        {
            Notice = T("Eingefügt – Ctrl+Z macht es rückgängig.");
        }
        else
        {
            Error = T("Der Text konnte nicht in die Mail übernommen werden.");
        }
    });

    /// <summary>The marked text – or without a selection the whole own text – rewritten (parameter: AiRewrite name).</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RewriteAsync(string how) => RunAsync(async (editor, cancel) =>
    {
        var mode = Enum.Parse<AiRewrite>(how);
        if (mode == AiRewrite.Custom && string.IsNullOrWhiteSpace(Instruction))
        {
            Error = T("Bitte oben eingeben, was geändert werden soll.");
            return;
        }

        var texts = await editor.GetTextsAsync();
        var selection = texts.Selection.Length > 0;
        var source = selection ? texts.Selection : texts.Own;
        if (string.IsNullOrWhiteSpace(source))
        {
            Error = T("Noch kein eigener Text – erst etwas schreiben oder «Schreiben» verwenden.");
            return;
        }

        if (!selection && !await MaySendWholeMailAsync(F("Es ist kein Text markiert. Neruna sendet deshalb Ihren ganzen Text an {0}.", ProviderName), cancel))
        {
            return;
        }

        var text = await CollectAsync(ai.RewriteAsync(source, mode, Instruction, cancel));
        if (await editor.ReplaceTextAsync(ToHtml(text) + (selection ? string.Empty : "<div><br></div>"), selection ? text : text + "\n\n", selection))
        {
            Notice = selection ? T("Markierten Text ersetzt – Ctrl+Z macht es rückgängig.") : T("Text ersetzt – Ctrl+Z macht es rückgängig.");
        }
        else
        {
            Error = T("Der Text konnte nicht in die Mail übernommen werden.");
        }
    });

    private bool IsIdle() => !IsBusy;

    private async Task RunAsync(Func<Editor.IHtmlEditor, CancellationToken, Task> task)
    {
        if (compose.Editor is not { } editor)
        {
            return;
        }

        Error = Notice = null;
        IsBusy = true;
        using var cancel = new CancellationTokenSource();
        _cancel = cancel.Cancel;
        try
        {
            await task(editor, cancel.Token);
        }
        catch (AiException ex)
        {
            Error = ex.Message;
        }
        catch (HttpRequestException)
        {
            Error = T("Die KI ist gerade nicht erreichbar.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        finally
        {
            _cancel = null;
            IsBusy = false;
        }
    }

    private static async Task<string> CollectAsync(IAsyncEnumerable<string> answer)
    {
        var text = new StringBuilder();
        await foreach (var piece in answer)
        {
            text.Append(piece);
        }

        return text.ToString().Trim();
    }

    // Models sometimes mark words with **…** despite being asked for plain text: the stars go.
    private static string ToHtml(string text) => MessageContent.PlainTextToHtml(Bold().Replace(text, "$1"));

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();
}
