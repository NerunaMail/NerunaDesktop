using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Neruna.Desktop.ViewModels;
using Neruna.Desktop.Views;

namespace Neruna.Desktop;

/// <summary>Maps view models to views explicitly (no reflection, so the app stays trimmable/AOT-friendly).</summary>
internal sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        MailViewModel => new MailView(),
        CalendarViewModel => new CalendarView(),
        ContactsViewModel => new ContactsView(),
        TasksViewModel => new TasksView(),
        TaskEditorViewModel => new TaskEditorView(),
        ChatViewModel => new ChatView(),
        AccountSetupViewModel => new AccountSetupView(),
        IcsSubscriptionViewModel => new IcsSubscriptionView(),
        SpecialCalendarViewModel => new SpecialCalendarView(),
        CloudAccountPasswordViewModel => new CloudAccountPasswordView(),
        CrashReportViewModel => new CrashReportView(),
        FolderSubscriptionsViewModel => new FolderSubscriptionsView(),
        SyncStatusViewModel => new SyncStatusView(),
        BackupChangesViewModel => new BackupChangesView(),
        AccountTransferDialogViewModel => new AccountTransferDialogView(),
        FileBackupViewModel => new FileBackupView(),
        AutoReplyViewModel => new AutoReplyView(),
        CalendarSelectionViewModel => new CalendarSelectionView(),
        ReadingPaneViewModel => new ReadingPaneView(),
        AddressBookSelectionViewModel => new AddressBookSelectionView(),
        ComposeViewModel => new ComposeView(),
        EventEditorViewModel => new EventEditorView(),
        ContactEditorViewModel => new ContactEditorView(),
        GroupEditorViewModel => new GroupEditorView(),
        AccountsViewModel => new AccountsView(),
        CertificatesViewModel => new CertificatesView(),
        SettingsViewModel => new SettingsView(),
        MailOptionsViewModel => new MailOptionsView(),
        CalendarOptionsViewModel => new CalendarOptionsView(),
        AiOptionsViewModel => new AiOptionsView(),
        SignaturesViewModel => new SignaturesView(),
        AppearanceViewModel => new AppearanceView(),
        CloudViewModel => new CloudView(),
        TextTemplatesViewModel => new TextTemplatesView(),
        QuickActionsViewModel => new QuickActionsView(),
        null => null,
        _ => new TextBlock { Text = "Keine Ansicht für " + param.GetType().Name },
    };

    public bool Match(object? data) => data is ViewModelBase;
}
