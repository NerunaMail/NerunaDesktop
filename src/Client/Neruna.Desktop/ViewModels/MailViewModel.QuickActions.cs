using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using Neruna.Core;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Where a quick action runs: the open mail's pane (main window or a message window), its list entry and folder, and
/// whether replies open in their own window.
/// </summary>
internal sealed record QuickActionContext(ReadingPaneViewModel Pane, MessageItemViewModel Item, MailFolderNode Folder, bool InWindow, bool Encrypted, Func<bool> IsOpen);

/// <summary>A quick action's button in the coloured bar, for the open mail.</summary>
internal sealed record QuickActionButton(QuickAction Action, System.Windows.Input.ICommand Command)
{
    public string Label => Action.Label;

    public string Color => Action.Color;
}

// Schnellaktionen: buttons for the open mail, and running their steps one after another.
internal sealed partial class MailViewModel
{
    private IReadOnlyList<QuickAction>? _quickActionList;

    /// <summary>The quick actions whose conditions the open mail meets (shown in the coloured bar).</summary>
    public ObservableCollection<QuickActionButton> QuickActionButtons { get; } = [];

    Task<IReadOnlyList<PdfStamp>> IAttachmentActions.GetStampsAsync() => quickActions.GetStampsAsync();

    async Task<string?> IAttachmentActions.GetStampNameAsync() => await quickActions.GetStampNameAsync() ?? Infrastructure.SystemUserName.Get();

    private MailFolderNode? OpenFolder => SelectedMessage is { } message ? FolderOf(message) : CurrentFolder;

    private async Task ShowQuickActionsAsync(ReadingPaneViewModel? pane)
    {
        QuickActionButtons.Clear();
        if (pane?.Message is null || OpenFolder is not { } folder)
        {
            return;
        }

        if (_quickActionList is null)
        {
            _quickActionList = await quickActions.GetActionsAsync();
            quickActions.Changed += async (_, _) =>
            {
                _quickActionList = await quickActions.GetActionsAsync();
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ShowQuickActionsAsync(ReadingPane));
            };
        }

        if (ReadingPane != pane)
        {
            return;
        }

        foreach (var action in MatchingQuickActions(pane, folder))
        {
            QuickActionButtons.Add(new QuickActionButton(action, RunQuickActionCommand));
        }
    }

    private IEnumerable<QuickAction> MatchingQuickActions(ReadingPaneViewModel pane, MailFolderNode folder)
    {
        var message = new QuickActionMessage(folder.Account.Account.Id, folder.Folder.RemoteId, pane.From, pane.Subject, [.. pane.Attachments.Select(a => a.Describe())]);
        return (_quickActionList ?? []).Where(a => QuickActionService.Matches(a, message));
    }

    // The PDF preview in a message window: stamps as in the main window, "Antworten/Weiterleiten mit PDF" for that mail.
    private sealed class WindowAttachmentActions(MailViewModel mail, QuickActionContext context) : IAttachmentActions
    {
        public Task<IReadOnlyList<PdfStamp>> GetStampsAsync() => ((IAttachmentActions)mail).GetStampsAsync();

        public Task<string?> GetStampNameAsync() => ((IAttachmentActions)mail).GetStampNameAsync();

        public async Task RespondWithAsync(AttachmentViewModel attachment, byte[] content, QuickActionStepKind kind)
        {
            if (context.Pane.Message is { } message)
            {
                await mail.RespondWithAttachmentsAsync(message, kind, [attachment], new Dictionary<AttachmentViewModel, byte[]> { [attachment] = content }, null, context);
            }
        }
    }

    private WindowAttachmentActions AttachmentActionsFor(QuickActionContext context) => new(this, context);

    /// <summary>The buttons for a mail in its own window (the same actions, run there).</summary>
    private async Task<IReadOnlyList<QuickActionButton>> QuickActionsForWindowAsync(QuickActionContext context)
    {
        _quickActionList ??= await quickActions.GetActionsAsync();
        var command = new AsyncRelayCommand<QuickAction?>(action => action is null ? Task.CompletedTask : RunQuickActionAsync(action, context));
        return [.. MatchingQuickActions(context.Pane, context.Folder).Select(a => new QuickActionButton(a, command))];
    }

    /// <summary>The PDF preview: answer or forward with the stamped PDF.</summary>
    async Task IAttachmentActions.RespondWithAsync(AttachmentViewModel attachment, byte[] content, QuickActionStepKind kind)
    {
        if (ReadingPane is { Message: { } message } pane && SelectedMessage is { } item && OpenFolder is { } folder)
        {
            await RespondWithAttachmentsAsync(message, kind, [attachment], new Dictionary<AttachmentViewModel, byte[]> { [attachment] = content }, null,
                new QuickActionContext(pane, item, folder, false, _opened?.Security.WasEncrypted == true, () => ReadingPane == pane));
        }
    }

    [RelayCommand]
    private Task RunQuickActionAsync(QuickAction? action) =>
        action is null || ReadingPane is not { Message: not null } pane || SelectedMessage is not { } item || OpenFolder is not { } folder
            ? Task.CompletedTask
            : RunQuickActionAsync(action, new QuickActionContext(pane, item, folder, false, _opened?.Security.WasEncrypted == true, () => ReadingPane == pane));

    private async Task RunQuickActionAsync(QuickAction action, QuickActionContext context)
    {
        var (pane, item, folder) = (context.Pane, context.Item, context.Folder);
        if (pane.Message is not { } message)
        {
            return;
        }

        var targets = QuickActionService.AttachmentsFor(action, pane.Attachments, a => a.Describe());
        var changed = new Dictionary<AttachmentViewModel, byte[]>();
        async Task<byte[]?> ContentOf(AttachmentViewModel attachment) =>
            changed.TryGetValue(attachment, out var content) ? content : await attachment.ReadAsync();

        var sender = message.From.Mailboxes.FirstOrDefault() is { } from ? (string.IsNullOrWhiteSpace(from.Name) ? from.Address : from.Name) : string.Empty;
        var subject = message.Subject ?? string.Empty;
        void Cancelled() => StatusMessage?.Invoke(this, F("Schnellaktion «{0}» abgebrochen.", action.Label));
        try
        {
            foreach (var (step, index) in action.Steps.Select((s, i) => (s, i)))
            {
                if (!context.IsOpen())
                {
                    return; // another mail was opened meanwhile (or its window closed)
                }

                switch (step.Kind)
                {
                    case QuickActionStepKind.StampPdf:
                        var stamps = await quickActions.GetStampsAsync();
                        if ((stamps.FirstOrDefault(s => s.Id == step.StampId) ?? stamps.FirstOrDefault()) is not { } stamp)
                        {
                            break;
                        }

                        foreach (var attachment in targets.Where(t => QuickActionService.IsPdf(t.Describe())))
                        {
                            if (await pane.StampAsync(attachment, stamp, changed.GetValueOrDefault(attachment)) is not { } stamped)
                            {
                                Cancelled();
                                return;
                            }

                            changed[attachment] = stamped;
                        }

                        break;

                    case QuickActionStepKind.SaveAttachment:
                        foreach (var attachment in targets)
                        {
                            // Cancelling the save dialog stops the action: the later steps (moving the mail away) assume it was saved.
                            if (await ContentOf(attachment) is { } content
                                && !await attachment.SaveContentAsync(content, QuickActionService.FileName(step.FileName, attachment.FileName, sender, subject, DateTime.Now), step.Folder))
                            {
                                Cancelled();
                                return;
                            }
                        }

                        break;

                    case QuickActionStepKind.Print:
                        foreach (var attachment in targets)
                        {
                            if (await ContentOf(attachment) is { } content)
                            {
                                await files.PrintAsync(attachment.FileName, content);
                            }
                        }

                        StatusMessage?.Invoke(this, targets.Count == 1 ? T("Anhang an den Drucker gesendet.") : F("{0} Anhänge an den Drucker gesendet.", targets.Count));
                        break;

                    case QuickActionStepKind.OpenAttachment:
                        foreach (var attachment in targets)
                        {
                            if (await ContentOf(attachment) is { } content)
                            {
                                if (!await files.OpenAsync(attachment.FileName, stream => stream.WriteAsync(content).AsTask()))
                                {
                                    Cancelled();
                                    return;
                                }
                            }
                        }

                        break;

                    case QuickActionStepKind.Reply or QuickActionStepKind.ReplyAll or QuickActionStepKind.Forward or QuickActionStepKind.NewMail:
                        var compose = await RespondWithAttachmentsAsync(message, step.Kind, targets, changed, step, context, sender, subject);

                        // The steps after it (mark read, move …) wait until the mail was sent; discarded, they do not run.
                        if (index < action.Steps.Count - 1)
                        {
                            if (compose is null || !await SentAsync(compose))
                            {
                                StatusMessage?.Invoke(this, F("Schnellaktion «{0}» angehalten – die E-Mail wurde nicht gesendet.", action.Label));
                                return;
                            }
                        }

                        break;

                    case QuickActionStepKind.MoveToFolder when step.AskFolder:
                        // Asked each time: account (the mail's own preselected) and folder; cancelling stops the action.
                        if (await windows.ShowFolderPickerAsync(new FolderPickerViewModel(Accounts, folder.Account, FolderOf(item) ?? folder)) is not { } chosen)
                        {
                            Cancelled();
                            return;
                        }

                        await MoveToFolderAsync([item], chosen);
                        break;

                    case QuickActionStepKind.MoveToFolder:
                        // The folder of the mail's account: by its id, else by its name (an action for several accounts).
                        var folders = folder.Account.AllFolders().ToList();
                        if ((folders.FirstOrDefault(f => f.Folder.RemoteId == step.FolderRemoteId)
                             ?? folders.FirstOrDefault(f => string.Equals(f.Name, step.FolderName, StringComparison.OrdinalIgnoreCase))) is { } target)
                        {
                            await MoveToFolderAsync([item], target);
                        }

                        break;

                    case QuickActionStepKind.Flag:
                        // Only the open mail, not whatever else is selected in the list.
                        if (!item.IsFlagged && FolderOf(item) is { } flagFolder)
                        {
                            await mail.SetFlagsAsync(flagFolder.Account.Connection, flagFolder.Folder, [item.Summary.RemoteId], MessageFlags.Flagged, true);
                            item.IsFlagged = true;
                        }

                        break;

                    case QuickActionStepKind.MarkRead:
                        await SetReadAsync(item, read: true);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Report(F("Schnellaktion «{0}» fehlgeschlagen", action.Label), ex);
        }
    }

    // Completes when the compose form closes: true when it was sent.
    private static Task<bool> SentAsync(ComposeViewModel compose)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        compose.Closed += (_, sent) => done.TrySetResult(sent);
        return done.Task;
    }

    // Answer, forward or a new mail with the attachments – stamped ones as stamped; a text template above the signature.
    private async Task<ComposeViewModel?> RespondWithAttachmentsAsync(MimeMessage message, QuickActionStepKind kind, IReadOnlyList<AttachmentViewModel> attachments,
        Dictionary<AttachmentViewModel, byte[]> changed, QuickActionStep? step, QuickActionContext context, string sender = "", string subject = "")
    {
        var account = context.Folder.Account.Account;

        MimeEntity PartOf(AttachmentViewModel a) => changed.TryGetValue(a, out var content) ? a.WithContent(content) : a.Entity;

        var draft = kind switch
        {
            // An answer carries what was changed (the stamped PDF) – sending back the sender's own files makes no sense.
            QuickActionStepKind.Reply or QuickActionStepKind.ReplyAll =>
                MessageComposer.Reply(message, OwnAddresses(account), kind == QuickActionStepKind.ReplyAll) with
                {
                    Attachments = [.. attachments.Where(changed.ContainsKey).Select(PartOf)],
                },
            QuickActionStepKind.Forward => ForwardDraft(message, attachments, PartOf),
            _ => ComposeDraft.Empty with
            {
                Subject = string.IsNullOrWhiteSpace(step?.Subject) ? subject : QuickActionService.Fill(step.Subject, sender, subject, DateTime.Now),
                Attachments = [.. attachments.Select(PartOf)],
            },
        };

        if (!string.IsNullOrWhiteSpace(step?.To) && kind is QuickActionStepKind.Forward or QuickActionStepKind.NewMail)
        {
            draft = draft with { To = step.To.Trim() };
        }

        // "Weiterleiten als …": a sender of its own, if the account still exists.
        if (step?.FromAccountId is { } fromAccount && Accounts.FirstOrDefault(a => a.Account.Id == fromAccount) is { } sending)
        {
            draft = draft with
            {
                FromAccountId = fromAccount,
                From = sending.Account.Owns(step.From) ? step.From : sending.Account.EmailAddress,
            };
        }

        if (step?.TemplateId is { } templateId && (await textTemplates.GetAllAsync()).FirstOrDefault(t => t.Id == templateId) is { } template)
        {
            // Placeholders such as {{recipient_last_name}} for the reply's recipient.
            var recipient = TemplateFields.HasFields(template.Html)
                ? await TemplateFields.ResolveAsync(draft.To, email => recipients.FindContactAsync(email))
                : null;
            draft = draft with { LeadHtml = TemplateFields.Fill(template.Html, recipient) };
        }

        if (kind == QuickActionStepKind.NewMail)
        {
            return await StartComposeAsync(draft, ComposeKind.New, inWindow: context.InWindow);
        }

        // Answered or forwarded: the original gets its mark once sent; and it counts as read.
        var composeKind = kind == QuickActionStepKind.Forward ? ComposeKind.Forward : ComposeKind.Reply;
        var compose = await StartComposeAsync(draft, composeKind, context.Encrypted, () => MarkRespondedAsync(context.Item, context.Folder, composeKind), context.InWindow);
        if (context.Item is { IsUnread: true } && await GetMarkAsReadModeAsync() != MarkAsReadMode.Never)
        {
            await SetReadAsync(context.Item, read: true);
        }

        return compose;
    }

    // All attachments of the mail go along, the changed ones in their new version.
    private static ComposeDraft ForwardDraft(MimeMessage message, IReadOnlyList<AttachmentViewModel> attachments, Func<AttachmentViewModel, MimeEntity> partOf)
    {
        var draft = MessageComposer.Forward(message);
        return draft with
        {
            Attachments = [.. draft.Attachments.Select(e => attachments.FirstOrDefault(a => ReferenceEquals(a.Entity, e)) is { } a ? partOf(a) : e)],
        };
    }
}
