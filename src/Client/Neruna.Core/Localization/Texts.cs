using System.Globalization;
using System.Text.Json;

namespace Neruna.Core.Localization;

/// <summary>
/// The app's languages (de, en, fr, it). German texts are written in the code and serve as keys (as with gettext);
/// <c>Localization/{en,fr,it}.json</c> map them to the other languages. Anything not translated stays German.
/// The language is chosen once at start (Einstellungen → Design, or the system's); a change needs a restart.
/// </summary>
public static class Texts
{
    // The system's formats, before Neruna sets its own.
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentCulture;
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;

    private static Dictionary<string, string> _catalog = [];

    /// <summary>Codes and names (in their own language) for the language choice.</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
        [("de", "Deutsch"), ("en", "English"), ("fr", "Français"), ("it", "Italiano")];

    /// <summary>"de", "en", "fr" or "it".</summary>
    public static string Language { get; private set; } = "de";

    /// <summary>Formats of the language: Swiss for German, French and Italian, British for English – unless the
    /// system's own culture has the same language (then its regional formats are kept).</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("de-CH");

    /// <summary>The language the system would pick: the OS UI language if Neruna has it, otherwise German.</summary>
    public static string SystemLanguage
    {
        get
        {
            var system = SystemUiCulture.TwoLetterISOLanguageName;
            return Languages.Any(l => l.Code == system) ? system : "de";
        }
    }

    /// <summary>Switches to <paramref name="language"/> ("de", "en", "fr", "it"; null or "auto" = system).</summary>
    public static void Use(string? language)
    {
        var code = language is null or "auto" || Languages.All(l => l.Code != language) ? SystemLanguage : language;
        Language = code;
        _catalog = code == "de" ? [] : Load(code);
        var system = SystemCulture;
        Culture = system.TwoLetterISOLanguageName == code && !system.IsNeutralCulture
            ? system
            : CultureInfo.GetCultureInfo(code switch { "en" => "en-GB", "fr" => "fr-CH", "it" => "it-CH", _ => "de-CH" });
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = Culture;
    }

    /// <summary>The German <paramref name="text"/> in the current language.</summary>
    public static string T(string text) => _catalog.TryGetValue(text, out var translated) ? translated : text;

    /// <summary><see cref="T"/> with <c>{0}</c>, <c>{1}</c> … filled in (formats of the current language).</summary>
    public static string F(string text, params object?[] args) => string.Format(Culture, T(text), args);

    /// <summary>The catalog of a language, for tests: German text → translation.</summary>
    public static IReadOnlyDictionary<string, string> CatalogOf(string language) => Load(language);

    private static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(Texts).Assembly.GetManifestResourceStream($"Neruna.Core.Localization.{language}.json");
        return stream is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
