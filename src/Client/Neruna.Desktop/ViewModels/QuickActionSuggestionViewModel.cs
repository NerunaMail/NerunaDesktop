using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>"Schnellaktion erstellen?" – after the third mail of a sender moved by hand into the same folder.</summary>
internal sealed partial class QuickActionSuggestionViewModel(QuickActionSuggestion suggestion, QuickActionSuggestions suggestions) : ViewModelBase
{
    /// <summary>Done: the created action's name, or null for "Nein".</summary>
    public event EventHandler<string?>? Finished;

    public string Text => F("Sie haben E-Mails von {0} mehrmals in den Ordner «{1}» verschoben. Soll Neruna daraus eine Schnellaktion erstellen, die Sie künftig mit einem Klick nutzen können?",
        suggestion.SenderName == suggestion.Sender ? suggestion.Sender : $"{suggestion.SenderName} <{suggestion.Sender}>", suggestion.FolderName);

    public string Steps => F("Die Schnellaktion markiert E-Mails von {0} als gelesen und verschiebt sie nach «{1}». Sie lässt sich unter Einstellungen → Schnellaktionen anpassen.",
        suggestion.Sender, suggestion.FolderName);

    /// <summary>"Bei diesem Absender nicht mehr fragen".</summary>
    [ObservableProperty]
    public partial bool Never { get; set; }

    [RelayCommand]
    private async Task YesAsync()
    {
        var action = await suggestions.AcceptAsync(suggestion);
        Finished?.Invoke(this, action.Label);
    }

    [RelayCommand]
    private async Task NoAsync()
    {
        await suggestions.DeclineAsync(suggestion, Never);
        Finished?.Invoke(this, null);
    }
}
