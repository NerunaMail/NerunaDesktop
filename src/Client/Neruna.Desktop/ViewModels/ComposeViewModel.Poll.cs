using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Calendar;
using Neruna.Core.Cloud;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Terminumfrage" while writing: a poll with the recipients in An and Cc as invitees; its link (and password) go into
/// the text at the caret. The answers come in Kalender → Terminumfragen, where the date is fixed.
/// </summary>
internal sealed partial class ComposeViewModel
{
    /// <summary>Set when Neruna is connected to Neruna Cloud (polls are answered on its web page).</summary>
    public SchedulingPolls? Polls { get; init; }

    public bool CanMakePoll => Polls is not null && Calendars is not null;

    [RelayCommand]
    private async Task MakePollAsync()
    {
        if (Polls is not { } polls || Calendars is not { } c)
        {
            return;
        }

        try
        {
            var connections = await polls.ConnectionsAsync();
            if (connections.Count == 0)
            {
                Notice?.Invoke(this, T("Für Terminumfragen muss Neruna zuerst mit einem Konto bei Neruna Cloud verbunden werden (Einstellungen → Cloud)."));
                return;
            }

            var editor = new PollEditorViewModel(polls, connections, null, Subject, EventAttendee.FromRecipients(To, Cc), forMail: true);
            if (!await c.Windows.ShowPollEditorAsync(editor) || editor.Saved is not { } poll || _editor is null)
            {
                return;
            }

            MarkChanged();
            if (string.IsNullOrWhiteSpace(Subject))
            {
                Subject = F("Terminumfrage: {0}", poll.Title);
            }

            var (html, text) = SchedulingPolls.InvitationText(poll, editor.SavedPassword);
            await _editor.InsertAsync(html, text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notice?.Invoke(this, T("Die Terminumfrage konnte nicht erstellt werden: ") + ex.Message);
        }
    }
}
