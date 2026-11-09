using System.Globalization;
using Newtonsoft.Json;

namespace Codex.StashQuickSell;

internal sealed class Localization
{
    private readonly Dictionary<string, Dictionary<string, string>> _languages = new();
    private readonly Dictionary<string, string> _fallbackEnglish;
    public Localization(string? directory = null, Action<string>? warning = null)
    {
        var assembly = typeof(Localization).Assembly;
        _fallbackEnglish = new();
        foreach (string language in new[] { "en", "ch" })
        {
            string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith($".i18n.{language}.json", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            _languages[language] = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd())!;
            if (language == "en")
                foreach (var entry in _languages[language]) _fallbackEnglish.Add(entry.Key, entry.Value);
            string? path = directory == null ? null : Path.Combine(directory, language + ".json");
            if (path == null || !File.Exists(path)) continue;
            try
            {
                var overrides = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                if (overrides != null)
                    foreach (var entry in overrides)
                        if (!string.IsNullOrWhiteSpace(entry.Value))
                        {
                            try
                            {
                                _ = string.Format(CultureInfo.InvariantCulture, entry.Value, new object[8]);
                                _languages[language][entry.Key] = entry.Value;
                            }
                            catch (FormatException) { warning?.Invoke($"Invalid translation format: {language}/{entry.Key}"); }
                        }
            }
            catch (Exception exception) { warning?.Invoke($"Could not load {path}: {exception.Message}"); }
        }
    }

    public static string Normalize(string? language) =>
        language != null && (language.Equals("ch", StringComparison.OrdinalIgnoreCase) ||
        language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) ? "ch" : "en";

    public string Text(string? language, string key, params object[] arguments)
    {
        var dictionary = _languages[Normalize(language)];
        if (!dictionary.TryGetValue(key, out var text) && !_languages["en"].TryGetValue(key, out text)) text = key;
        try { return string.Format(CultureInfo.InvariantCulture, text, arguments); }
        catch (FormatException) { return _fallbackEnglish.TryGetValue(key, out var fallback)
            ? string.Format(CultureInfo.InvariantCulture, fallback, arguments) : key; }
    }
}
