using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Win32;

namespace CameraInspector;

internal enum AppTheme { Light, Dark, System }

public partial class App : Application
{
    private const string WindowsThemeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private string _themePath = null!;
    private ResourceDictionary? _palette;

    internal AppTheme PreferredTheme { get; private set; } = AppTheme.System;

    protected override void OnStartup(StartupEventArgs e)
    {
        LoadThemePreference(Path.Combine(Path.GetDirectoryName(Languages.PreferencePath)!, "theme.txt"));
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        base.OnExit(e);
    }

    internal void LoadThemePreference(string path)
    {
        _themePath = path;
        PreferredTheme = AppTheme.System;
        try
        {
            PreferredTheme = File.ReadAllText(path).Trim() switch
            {
                nameof(AppTheme.Light) => AppTheme.Light,
                nameof(AppTheme.Dark) => AppTheme.Dark,
                _ => AppTheme.System
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(error);
        }
        ApplyTheme();
    }

    internal void ChangeTheme(AppTheme theme)
    {
        PreferredTheme = theme;
        ApplyTheme();
        PreferenceFile.Write(_themePath, theme.ToString());
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (PreferredTheme == AppTheme.System)
                ApplyTheme();
        });
    }

    private void ApplyTheme()
    {
        bool dark = PreferredTheme == AppTheme.Dark || (PreferredTheme == AppTheme.System && WindowsUsesDarkTheme());
        var source = new Uri($"/CameraInspector;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative);
        if (_palette?.Source == source)
            return;

        var dictionaries = Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = source };
        if (_palette is null)
            dictionaries.Add(palette);
        else
            dictionaries[dictionaries.IndexOf(_palette)] = palette;
        _palette = palette;

#pragma warning disable WPF0001 // WPF handles native window chrome and default controls.
        ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
    }

    private static bool WindowsUsesDarkTheme()
    {
        try
        {
            return Registry.GetValue(WindowsThemeKey, "AppsUseLightTheme", 1) is 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            Debug.WriteLine(error);
            return false;
        }
    }

    internal static bool TryOpenLink(RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
