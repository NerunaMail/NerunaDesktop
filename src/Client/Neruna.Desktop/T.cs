using Avalonia.Markup.Xaml;
using Neruna.Core.Localization;

namespace Neruna.Desktop;

/// <summary>
/// <c>{l:T 'Neue E-Mail'}</c> in XAML: the German text in the language chosen at start (see <see cref="Texts"/>).
/// </summary>
internal sealed class TExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => Texts.T(Text);
}

/// <summary>
/// <c>{Binding X, Converter={x:Static l:Fmt.Instance}, ConverterParameter='Passwort für {0}'}</c>: the German format
/// translated, then filled with the bound value.
/// </summary>
internal sealed class Fmt : Avalonia.Data.Converters.IValueConverter
{
    public static Fmt Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        parameter is string format ? Texts.F(format, value) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
