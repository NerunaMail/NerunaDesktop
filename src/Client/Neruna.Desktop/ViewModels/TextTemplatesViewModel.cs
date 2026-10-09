using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Mail;
using Neruna.Desktop.Infrastructure;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Einstellungen → Textvorlagen: own text templates (create, edit, delete) and the organisation's from Neruna Cloud
/// (read-only). Inserted at the caret while writing ("Textvorlage" in the compose toolbar).
/// </summary>
internal sealed partial class TextTemplatesViewModel(TextTemplateService templates, ISettingsStore settings, IFileService files, IWindowService windows) : ViewModelBase
{
    public ObservableCollection<TextTemplate> Templates { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewHtml), nameof(IsCloudSelected))]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand))]
    public partial TextTemplate? Selected { get; set; }

    public bool HasTemplates => Templates.Count > 0;

    public bool IsCloudSelected => Selected?.IsFromCloud == true;

    public string PreviewHtml => Selected is null ? string.Empty : $"<html><body>{Selected.Html}</body></html>";

    public async Task ReloadAsync()
    {
        var selectedId = Selected?.Id;
        Templates.Clear();
        foreach (var template in await templates.GetAllAsync())
        {
            Templates.Add(template);
        }

        Selected = Templates.FirstOrDefault(t => t.Id == selectedId) ?? Templates.FirstOrDefault();
        OnPropertyChanged(nameof(HasTemplates));
    }

    [RelayCommand]
    private Task NewAsync() => OpenEditorAsync(new TextTemplate(Guid.NewGuid(), Templates.Count == 0 ? "Anrufnotiz" : $"Textvorlage {Templates.Count + 1}", string.Empty, DateTimeOffset.Now), isNew: true);

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task EditAsync() => OpenEditorAsync(Selected!, isNew: false);

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task DeleteAsync()
    {
        await templates.DeleteAsync(Selected!.Id);
        Selected = null;
        await ReloadAsync();
    }

    private bool CanModify() => Selected is { IsFromCloud: false };

    // The signature editor, with the texts of a text template.
    private async Task OpenEditorAsync(TextTemplate template, bool isNew)
    {
        var editor = new SignatureEditorViewModel(new Signature(template.Id, template.Name, template.Html, template.UpdatedAt), isNew, files, settings, isTextTemplate: true)
        {
            Shortcut = template.Shortcut,
            ValidateShortcut = async shortcut => await templates.IsShortcutTakenAsync(shortcut, template.Id)
                ? $"Das Kürzel «{TextTemplate.NormalizeShortcut(shortcut)}» hat bereits eine andere Textvorlage."
                : null,
        };
        if (await windows.ShowSignatureEditorAsync(editor) is { } saved)
        {
            await templates.SaveAsync(template with { Name = saved.Name, Html = saved.Html, UpdatedAt = saved.UpdatedAt, Shortcut = TextTemplate.NormalizeShortcut(editor.Shortcut) });
            Selected = null;
            await ReloadAsync();
            Selected = Templates.FirstOrDefault(t => t.Id == template.Id);
        }
    }
}
