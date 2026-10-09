using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Neruna.Core.Chat;

namespace Neruna.Desktop.Controls;

/// <summary>
/// A chat message with its simple formatting: **bold** and __underlined__ (also nested); everything else as typed.
/// Selectable, so parts can be copied.
/// </summary>
public sealed class ChatText : SelectableTextBlock
{
    public static readonly StyledProperty<string?> MarkupProperty = AvaloniaProperty.Register<ChatText, string?>(nameof(Markup));

    static ChatText() => MarkupProperty.Changed.AddClassHandler<ChatText>((c, _) => c.Build());

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public string? Markup
    {
        get => GetValue(MarkupProperty);
        set => SetValue(MarkupProperty, value);
    }

    private void Build()
    {
        var inlines = new InlineCollection();
        foreach (var (text, bold, underline) in ChatFormatting.Parse(Markup ?? string.Empty))
        {
            var run = new Run(text);
            if (bold)
            {
                run.FontWeight = FontWeight.Bold;
            }

            if (underline)
            {
                run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
            }

            inlines.Add(run);
        }

        Inlines = inlines;
    }
}
