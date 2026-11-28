using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Navigation;

namespace CameraInspector;

internal partial class SettingsWindow : Window
{
    private static readonly string AppVersion = typeof(SettingsWindow).Assembly.GetName().Version!.ToString(3);
    private CultureInfo _culture = null!;
    private string _browserErrorFormat = "";
    private Uri? _failedLink;

    internal SettingsWindow(LanguageOption language, IReadOnlyDictionary<string, string> messages,
        Func<LanguageOption, IReadOnlyDictionary<string, string>> changeLanguage)
    {
        InitializeComponent();
        ApplyLanguage(language, messages);
        LanguagePicker.ItemsSource = Languages.Options.Prepend(Languages.System);
        LanguagePicker.SelectedItem = language;
        LanguagePicker.SelectionChanged += (_, _) =>
        {
            if (LanguagePicker.SelectedItem is LanguageOption option)
                ApplyLanguage(option, changeLanguage(option));
        };
        var app = (App)Application.Current;
        ThemePicker.ItemsSource = Enum.GetValues<AppTheme>();
        ThemePicker.SelectedItem = app.PreferredTheme;
        ThemePicker.SelectionChanged += (_, _) =>
        {
            if (ThemePicker.SelectedItem is AppTheme theme)
                app.ChangeTheme(theme);
        };
    }

    private void ApplyLanguage(LanguageOption language, IReadOnlyDictionary<string, string> messages)
    {
        _culture = Languages.Resolve(language).Culture;
        _browserErrorFormat = messages["BrowserFailed"];
        Language = XmlLanguage.GetLanguage(_culture.IetfLanguageTag);
        FlowDirection = _culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (string key in new[] { "Settings", "LanguageLabel", "ThemeLabel", "ThemeLight", "ThemeDark", "SystemOption",
            "About", "CameraDiagnostics", "SupportedCameras", "CheckForUpdates", "Close" })
            Resources[key] = messages[key];
        VersionText.Text = string.Format(_culture, messages["Version"], AppVersion);
        ShowBrowserError();
    }

    private void OnOpenLink(object sender, RequestNavigateEventArgs e)
    {
        _failedLink = App.TryOpenLink(e) ? null : e.Uri;
        ShowBrowserError();
    }

    private void ShowBrowserError()
    {
        BrowserError.Text = _failedLink is null ? "" : string.Format(_culture, _browserErrorFormat, _failedLink.AbsoluteUri);
        BrowserError.Visibility = _failedLink is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
