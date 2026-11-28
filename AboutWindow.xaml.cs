using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Navigation;

namespace CameraInspector;

internal partial class AboutWindow : Window
{
    private static readonly string AppVersion = typeof(AboutWindow).Assembly.GetName().Version!.ToString(3);
    private readonly CultureInfo _culture;
    private readonly string _browserErrorFormat;

    internal AboutWindow(LanguageOption language, IReadOnlyDictionary<string, string> messages)
    {
        InitializeComponent();
        _culture = language.Culture;
        _browserErrorFormat = messages["BrowserFailed"];
        Language = XmlLanguage.GetLanguage(_culture.IetfLanguageTag);
        FlowDirection = _culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (string key in new[] { "About", "SupportedCameras", "CheckForUpdates", "Close" })
            Resources[key] = messages[key];
        VersionText.Text = string.Format(_culture, messages["Version"], AppVersion);
    }

    private void OnOpenLink(object sender, RequestNavigateEventArgs e)
    {
        bool opened = App.TryOpenLink(e);
        BrowserError.Text = opened ? "" : string.Format(_culture, _browserErrorFormat, e.Uri.AbsoluteUri);
        BrowserError.Visibility = opened ? Visibility.Collapsed : Visibility.Visible;
    }
}
