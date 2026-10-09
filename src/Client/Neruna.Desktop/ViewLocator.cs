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
        ChatViewModel => new ChatView(),
        AccountSetupViewModel => new AccountSetupView(),
        IcsSubscriptionViewModel => new IcsSubscriptionView(),
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
        SignaturesViewModel => new SignaturesView(),
        AppearanceViewModel => new AppearanceView(),
        CloudViewModel => new CloudView(),
        TextTemplatesViewModel => new TextTemplatesView(),
        null => null,
        _ => new TextBlock { Text = "Keine Ansicht für " + param.GetType().Name },
    };

    public bool Match(object? data) => data is ViewModelBase;
}
