using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace CameraInspector;

internal sealed record LanguageOption(string Code, string Name, string CultureName)
{
    internal CultureInfo Culture => CultureInfo.GetCultureInfo(CultureName);

    public override string ToString() => Name;
}

internal static class Languages
{
    internal static LanguageOption System { get; } = new("system", "System", "");

    internal static IReadOnlyList<LanguageOption> Options { get; } =
    [
        new("ar", "العربية", "ar-SA"),
        new("bn", "বাংলা", "bn-BD"),
        new("zh", "简体中文", "zh-CN"),
        new("arz", "العربية المصرية", "ar-EG"),
        new("en", "English", "en-US"),
        new("fr", "Français", "fr-FR"),
        new("de", "Deutsch", "de-DE"),
        new("hi", "हिन्दी", "hi-IN"),
        new("id", "Bahasa Indonesia", "id-ID"),
        new("ja", "日本語", "ja-JP"),
        new("pcm", "Naijíriá Píjin", "en-NG"),
        new("pt", "Português (Brasil)", "pt-BR"),
        new("ru", "Русский", "ru-RU"),
        new("es", "Español", "es-ES"),
        new("uk", "Українська", "uk-UA"),
        new("ur", "اردو", "ur-PK")
    ];

    internal static string PreferencePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Camera Info", "language.txt");

    internal static IReadOnlyDictionary<string, string> Catalog(LanguageOption option)
    {
        using var stream = typeof(Languages).Assembly.GetManifestResourceStream($"CameraInspector.Locales.{option.Code}.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    internal static LanguageOption Resolve(LanguageOption preference)
    {
        if (preference != System)
            return preference;

        for (var culture = CultureInfo.CurrentUICulture; culture.Name.Length > 0; culture = culture.Parent)
        {
            var match = Options.FirstOrDefault(option => option.Code.Equals(culture.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }
        return Options.Single(option => option.Code == "en");
    }

    internal static LanguageOption ReadPreference(string path)
    {
        string code = System.Code;
        try { code = File.ReadAllText(path).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(error);
        }
        return Options.FirstOrDefault(option => option.Code == code) ?? System;
    }

    internal static void SavePreference(string path, LanguageOption option) => PreferenceFile.Write(path, option.Code);
}
