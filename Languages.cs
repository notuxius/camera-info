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

    internal static LanguageOption ReadPreference(string path)
    {
        string code = "en";
        try { code = File.ReadAllText(path).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(error);
        }
        return Options.FirstOrDefault(option => option.Code == code) ?? Options.Single(option => option.Code == "en");
    }

    internal static void SavePreference(string path, LanguageOption option)
    {
        string directory = Path.GetDirectoryName(path)!;
        string temporary = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(temporary, option.Code);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(error);
        }
    }
}
