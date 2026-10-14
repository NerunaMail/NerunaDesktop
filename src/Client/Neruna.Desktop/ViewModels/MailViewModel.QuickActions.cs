using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

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

    // The account of the open mail (a search may show mails of several).
    private Neruna.Core.Accounts.Account? OpenAccount => OpenFolder?.Account.Account ?? CurrentAccount?.Account;

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

        var message = new QuickActionMessage(folder.Account.Account.Id, folder.Folder.RemoteId, pane.From, pane.Subject, [.. pane.Attachments.Select(a => a.Describe())]);
        foreach (var action in _quickActionList.Where(a => QuickActionService.Matches(a, message)))
        {
            QuickActionButtons.Add(new QuickActionButton(action, RunQuickActionCommand));
        }
    }

    /// <summary>The PDF preview: answer or forward with the stamped PDF.</summary>
    async Task IAttachmentActions.RespondWithAsync(AttachmentViewModel attachment, byte[] content, QuickActionStepKind kind)
    {
        if (ReadingPane?.Message is { } message)
        {
            await RespondWithAttachmentsAsync(message, kind, [attachment], new Dictionary<AttachmentViewModel, byte[]> { [attachment] = content }, null);
        }
    }

    [RelayCommand]
    private async Task RunQuickActionAsync(QuickAction? action)
    {
        if (action is null || ReadingPane is not { Message: { } message } pane || SelectedMessage is not { } item)
        {
            return;
        }

        var targets = QuickActionService.AttachmentsFor(action, pane.Attachments, a => a.Describe());
        var changed = new Dictionary<AttachmentViewModel, byte[]>();
        async Task<byte[]?> ContentOf(AttachmentViewModel attachment) =>
            changed.TryGetValue(attachment, out var content) ? content : await attachment.ReadAsync();

        var sender = message.From.Mailboxes.FirstOrDefault() is { } from ? (string.IsNullOrWhiteSpace(from.Name) ? from.Address : from.Name) : string.Empty;
        var subject = message.Subject ?? string.Empty;
        try
        {
            foreach (var step in action.Steps)
            {
                if (ReadingPane != pane)
                {
                    return; // another mail was opened meanwhile
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
                                StatusMessage?.Invoke(this, F("Schnellaktion «{0}» abgebrochen.", action.Label));
                                return;
                            }

                            changed[attachment] = stamped;
                        }

                        break;

                    case QuickActionStepKind.SaveAttachment:
                        foreach (var attachment in targets)
                        {
                            if (await ContentOf(attachment) is { } content)
                            {
                                await attachment.SaveContentAsync(content, QuickActionService.FileName(step.FileName, attachment.FileName, sender, subject, DateTime.Now), step.Folder);
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
                                await files.OpenAsync(attachment.FileName, stream => stream.WriteAsync(content).AsTask());
                            }
                        }

                        break;

                    case QuickActionStepKind.Reply or QuickActionStepKind.ReplyAll or QuickActionStepKind.Forward or QuickActionStepKind.NewMail:
                        await RespondWithAttachmentsAsync(message, step.Kind, targets, changed, step, sender, subject);
                        break;

                    case QuickActionStepKind.MoveToFolder:
                        // The folder of the mail's account: by its id, else by its name (an action for several accounts).
                        var folders = OpenFolder?.Account.AllFolders().ToList() ?? [];
                        if ((folders.FirstOrDefault(f => f.Folder.RemoteId == step.FolderRemoteId)
                             ?? folders.FirstOrDefault(f => string.Equals(f.Name, step.FolderName, StringComparison.OrdinalIgnoreCase))) is { } target)
                        {
                            await MoveToFolderAsync([item], target);
                        }

                        break;

                    case QuickActionStepKind.Flag:
                        if (!item.IsFlagged)
                        {
                            await ToggleFlagCommand.ExecuteAsync(null);
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

    // Answer, forward or a new mail with the attachments – stamped ones as stamped; a text template above the signature.
    private async Task RespondWithAttachmentsAsync(MimeMessage message, QuickActionStepKind kind, IReadOnlyList<AttachmentViewModel> attachments,
        Dictionary<AttachmentViewModel, byte[]> changed, QuickActionStep? step, string sender = "", string subject = "")
    {
        if (OpenAccount is not { } account)
        {
            return;
        }

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

        if (step?.TemplateId is { } templateId && (await textTemplates.GetAllAsync()).FirstOrDefault(t => t.Id == templateId) is { } template)
        {
            draft = draft with { LeadHtml = template.Html };
        }

        if (kind == QuickActionStepKind.NewMail)
        {
            await StartComposeAsync(draft, ComposeKind.New);
        }
        else
        {
            await RespondAsync(draft, kind == QuickActionStepKind.Forward ? ComposeKind.Forward : ComposeKind.Reply);
        }
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
