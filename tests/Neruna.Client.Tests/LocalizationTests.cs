using System.Text.RegularExpressions;
using Neruna.Core.Localization;

namespace Neruna.Client.Tests;

/// <summary>Every German text in the client (C# T/F calls, XAML {l:T '…'}) has its English, French and Italian version.</summary>
public partial class LocalizationTests
{
    [Fact]
    public void Every_text_is_translated_with_the_same_placeholders()
    {
        var texts = UsedTexts();
        Assert.True(texts.Count > 500, $"only {texts.Count} texts found – scanning broken?");
        foreach (var language in new[] { "en", "fr", "it" })
        {
            var catalog = Texts.CatalogOf(language);
            var missing = texts.Where(t => !catalog.ContainsKey(t)).ToList();
            Assert.True(missing.Count == 0, $"{language}: {missing.Count} untranslated, e.g. «{string.Join("», «", missing.Take(5))}»");
            foreach (var text in texts)
            {
                Assert.True(Placeholders(text).SequenceEqual(Placeholders(catalog[text])), $"{language}: placeholders differ in «{text}» → «{catalog[text]}»");
            }
        }
    }

    [Fact]
    public void The_language_switches_texts_and_formats()
    {
        try
        {
            Texts.Use("fr");
            Assert.Equal("fr", Texts.Language);
            Assert.Equal("Répondre", Texts.T("Antworten"));
            Assert.Equal("Synchronisé à 14:05", Texts.F("Synchronisiert um {0:t}", new DateTime(2026, 10, 9, 14, 5, 0)));
            Assert.Equal("Kein Text mit dieser Übersetzung", Texts.T("Kein Text mit dieser Übersetzung"));
            Texts.Use("it");
            Assert.Equal("Rispondi", Texts.T("Antworten"));
        }
        finally
        {
            Texts.Use("de");
        }

        Assert.Equal("Antworten", Texts.T("Antworten"));
    }

    private static List<string> UsedTexts()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Client"));
        var texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("\\obj\\", StringComparison.Ordinal)))
        {
            foreach (Match m in CallPattern().Matches(File.ReadAllText(file)))
            {
                texts.Add(Regex.Unescape(m.Groups[1].Value));
            }
        }

        foreach (var file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            var xaml = File.ReadAllText(file);
            foreach (Match m in XamlPattern().Matches(xaml))
            {
                texts.Add(System.Net.WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
            }
        }

        return [.. texts];
    }

    private static string[] Placeholders(string text) => [.. PlaceholderPattern().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal)];

    [GeneratedRegex("""(?<![\w.])(?:Texts\.)?[TF]\("((?:[^"\\]|\\.)*)"[,)]""")]
    private static partial Regex CallPattern();

    [GeneratedRegex("""\{l:T '([^']*)'\}|ConverterParameter='([^']*)'""")]
    private static partial Regex XamlPattern();

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
