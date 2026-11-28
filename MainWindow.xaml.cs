using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Navigation;
using Microsoft.Win32;

namespace CameraInspector;

public partial class MainWindow : Window
{
    private CameraReading? _reading;
    private ShutterComparison? _shutterComparison;
    private ShutterCount? _manual;
    private double? _ratio;
    private bool _busy;
    private bool _updatingList;
    private string _connectionKey = "NoCameraDetected";
    private (string Key, string? Argument)? _helpOverride;
    private readonly string _languagePath;
    private readonly string _historyDirectory;
    private readonly Func<List<CameraDevice>> _findCameras;
    private readonly Func<CameraDevice, CameraReading> _readCamera;
    private LanguageOption _language = null!;
    private IReadOnlyDictionary<string, string> _messages = null!;

    public MainWindow() : this(Languages.PreferencePath) { }

    internal MainWindow(string languagePath) : this(languagePath, ShutterHistory.DirectoryPath, WindowsCamera.Find, WindowsCamera.Read) { }

    internal MainWindow(string languagePath, string historyDirectory, Func<List<CameraDevice>> findCameras,
        Func<CameraDevice, CameraReading> readCamera)
    {
        _languagePath = languagePath;
        _historyDirectory = historyDirectory;
        _findCameras = findCameras;
        _readCamera = readCamera;
        InitializeComponent();
        SetLanguage(Languages.ReadPreference(languagePath));
        WindowPlacement.Attach(this, Path.Combine(Path.GetDirectoryName(languagePath)!, "window.json"));
    }

    private IReadOnlyDictionary<string, string> ChangeLanguage(LanguageOption option)
    {
        SetLanguage(option);
        Languages.SavePreference(_languagePath, option);
        return _messages;
    }

    private void SetLanguage(LanguageOption option)
    {
        _language = option;
        _messages = Languages.Catalog(option);
        foreach (var (key, message) in _messages)
            Resources[key] = message;

        Language = XmlLanguage.GetLanguage(option.Culture.IetfLanguageTag);
        FlowDirection = option.Culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ConnectionText.Text = T(_connectionKey);
        if (ManualError.Visibility == Visibility.Visible)
            ManualError.Text = T("InvalidCount");
        ShowReading();
    }

    private string T(string key, params object?[] values) =>
        values.Length == 0 ? _messages[key] : string.Format(_language.Culture, _messages[key], values);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ScanAsync();
        if (SizeToContent == SizeToContent.Height && IsVisible)
        {
            UpdateLayout();
            Height = RestoreBounds.Height;
            SizeToContent = SizeToContent.Manual;
        }
    }

    internal SettingsWindow CreateSettingsWindow() => new(_language, _messages, ChangeLanguage);

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = CreateSettingsWindow();
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    private async void OnScan(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void OnOpenPhoto(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = T("OpenPhoto"),
            Filter = "NEF, RAF, ARW, PEF, DNG, JPEG|*.nef;*.raf;*.arw;*.pef;*.dng;*.jpg;*.jpeg"
        };
        if (dialog.ShowDialog(this) == true)
            await ReadPhotoAsync(dialog.FileName);
    }

    internal async Task ReadPhotoAsync(string path)
    {
        if (_busy)
            return;

        SetBusy(true);
        SetConnection("OpeningPhoto");
        try
        {
            var reading = await Task.Run(() => Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".nef" => NikonNef.Read(path),
                ".raf" => FujifilmRaf.Read(path),
                ".arw" => SonyArw.Read(path),
                ".pef" => PentaxRaw.Read(path, ReadingSource.Pef),
                ".dng" => PentaxRaw.Read(path, ReadingSource.Dng),
                ".jpg" or ".jpeg" => CameraJpeg.Read(path),
                _ => throw new InvalidDataException("Unsupported photo file format.")
            });
            ResetManual();
            _reading = reading;
            _shutterComparison = null;
            DevicePicker.SelectedItem = null;
            ShowReading();
            SetConnection("PhotoOpened", "Green");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetConnection("PhotoOpenFailed");
            SetHelpOverride("PhotoFormatsHelp");
            Debug.WriteLine(error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ScanAsync()
    {
        if (_busy)
            return;

        var previous = (DevicePicker.SelectedItem as CameraDevice)?.Id;
        SetBusy(true);
        DevicePicker.Visibility = Visibility.Collapsed;
        _reading = null;
        ResetManual();
        ShowReading();
        SetConnection("Scanning");

        try
        {
            var cameras = await Task.Run(_findCameras);
            var camera = cameras.FirstOrDefault(item => item.Id == previous) ?? cameras.FirstOrDefault();
            _updatingList = true;
            DevicePicker.ItemsSource = cameras;
            DevicePicker.SelectedItem = camera;
            _updatingList = false;
            DevicePicker.Visibility = cameras.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            if (camera is null)
                SetConnection("NoCameraDetected");
            else
                await ReadAsync(camera);
        }
        catch (Exception error) when (IsUsbError(error))
        {
            SetConnection("ScanFailed");
            SetHelpOverride("ScanFailedHelp");
            Debug.WriteLine(error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingList || _busy || DevicePicker.SelectedItem is not CameraDevice camera)
            return;

        SetBusy(true);
        try { await ReadAsync(camera); }
        finally { SetBusy(false); }
    }

    private async Task ReadAsync(CameraDevice device)
    {
        _reading = null;
        ResetManual();
        ShowReading();
        SetConnection("ReadingCamera");

        try
        {
            var (reading, comparison) = await Task.Run(() =>
            {
                var reading = _readCamera(device);
                return (reading, ShutterHistory.TryRecord(reading, _historyDirectory));
            });
            ShowUsbReading(reading, comparison);
        }
        catch (CameraDisconnectedException error)
        {
            var remaining = DevicePicker.Items.Cast<CameraDevice>().Where(camera => camera.Id != device.Id).ToList();
            DevicePicker.ItemsSource = remaining;
            DevicePicker.SelectedItem = null;
            DevicePicker.Visibility = remaining.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SetConnection("CameraDisconnected");
            Debug.WriteLine(error);
        }
        catch (Exception error) when (IsUsbError(error))
        {
            SetConnection("OpenFailed");
            SetHelpOverride("OpenFailedHelp");
            Debug.WriteLine(error);
        }
    }

    private static bool IsUsbError(Exception error) =>
        error is COMException or UnauthorizedAccessException or InvalidCastException or IOException;

    internal void ShowUsbReading(CameraReading reading, ShutterComparison? comparison)
    {
        _reading = reading;
        _shutterComparison = comparison;
        ShowReading();
        SetConnection("UsbConnected", "Green");
    }

    private void ShowReading()
    {
        var rating = _reading is null ? null : CameraRatings.For(_reading.Model);
        var count = _manual ?? _reading?.Count;
        var usage = ShutterUsage.From(count, rating);
        _ratio = usage?.Ratio;

        bool hasModel = _reading is { Model: not CameraDevice.UnknownName };
        ModelTitle.FlowDirection = hasModel ? FlowDirection.LeftToRight : FlowDirection;
        ModelTitle.TextAlignment = hasModel && FlowDirection == FlowDirection.RightToLeft ? TextAlignment.Right : TextAlignment.Left;
        ModelTitle.Text = _reading?.Model switch
        {
            null => T("NoCameraConnected"),
            CameraDevice.UnknownName => T("UnknownCamera"),
            var model => model
        };
        MakerValue.Text = _reading?.Maker ?? "—";
        SerialValue.Text = _reading?.Serial ?? "—";
        FirmwareValue.Text = _reading?.Firmware ?? "—";
        BatteryValue.Text = _reading?.Battery is uint battery ? FormatLocalizedPercent(battery / 100m, "P0") : "—";
        bool isPhoto = _reading is { Source: not ReadingSource.Usb };
        string? photoFormat = _reading is { Source: ReadingSource.Usb, Count: null } ? PhotoFormatFor(_reading) : null;
        SourceValue.Text = _reading is null ? "—" : _manual is not null ? T("ManualCount")
            : _reading.Source.ToString().ToUpperInvariant();
        RatingLink.NavigateUri = rating is null ? null : new Uri(rating.Source);
        RatingLinkRow.Visibility = rating is null ? Visibility.Collapsed : Visibility.Visible;

        CountText.Text = count is { } value ? FormatCount(value.Value, value.IsUpperBound) : "—";
        ReadingLabel.Text = T(count?.Kind switch
        {
            CountKind.Mechanical => "MechanicalCount",
            CountKind.Image => "ImageCount",
            _ => "ReportedCount"
        });
        RatingText.Text = rating is null ? T("NoRating") : T("Rating", rating.Cycles);
        PercentText.Text = usage is { } measured
            ? count is { HasRange: true } bound && rating is not null
                ? "≤ " + FormatLocalizedPercent(Math.Ceiling(1000m * bound.Value / rating.Cycles) / 1000m, "P1")
                : FormatPercent(measured.Ratio)
            : "";
        ZoneText.Text = T(usage?.Zone switch
        {
            UsageZone.Low => "LowUsage",
            UsageZone.Elevated => "ElevatedUsage",
            UsageZone.High => "HighUsage",
            _ => _reading is null ? "AwaitingCamera" : "NoUsage"
        });
        ZoneText.Foreground = (System.Windows.Media.Brush)FindResource(usage?.Zone switch
        {
            UsageZone.Low => "Green",
            UsageZone.Elevated => "Yellow",
            UsageZone.High => "Red",
            _ => "Secondary"
        });
        UpdateMarker();

        UsageNote.Text = T((usage, _reading, count) switch
        {
            (not null, _, _) => "UsageBenchmark",
            (_, null, _) => "UsageComparison",
            (_, _, { Kind: CountKind.Unspecified }) => "UsageUnspecified",
            (_, _, { Kind: CountKind.Image }) => "UsageImageCount",
            (_, _, null) => isPhoto ? "PhotoCountUnavailable" : "NoUsbCount",
            _ => "UsageNeedsRating"
        });
        if (count is { HasRange: true } interval)
        {
            string rangeNote = T("CountRange", interval.LowerBound, interval.Value);
            UsageNote.Text = rangeNote + (rating is null ? " " + T("UsageNeedsRating")
                : usage is null ? " " + T("RangeSpansZones") : "");
        }
        ShowShutterHistory();

        bool useFujifilmXApp = _reading?.Maker.StartsWith("Fujifilm", StringComparison.OrdinalIgnoreCase) == true;
        bool allowManual = (rating is not null || useFujifilmXApp) &&
            _reading?.Count is not { Kind: CountKind.Mechanical, HasRange: false };
        ManualPanel.Visibility = allowManual ? Visibility.Visible : Visibility.Collapsed;
        ClearCountButton.Visibility = _manual is null ? Visibility.Collapsed : Visibility.Visible;
        HelpTitle.Text = T(_reading is null ? "ConnectTitle"
            : allowManual && (photoFormat is null || useFujifilmXApp) ? "ManualTitle" : "RefreshTitle");
        if (_helpOverride is { } help)
            HelpText.Text = T(help.Key, help.Argument);
        else if (_reading is null)
            HelpText.Text = T("ConnectHelp") + " " + T("PhotoFormatsHelp");
        else if (allowManual && useFujifilmXApp)
            HelpText.Text = T("FujifilmManualHelp");
        else if (photoFormat is not null)
            HelpText.Text = T("UsbPhotoHelp", photoFormat) + (allowManual ? " " + T("ManualHelp") : "");
        else
            HelpText.Text = T(allowManual ? "ManualHelp" : isPhoto ? "PhotoReadingHelp" : "RefreshHelp");
    }

    private static string? PhotoFormatFor(CameraReading reading)
    {
        if (reading.Maker.StartsWith("Nikon", StringComparison.OrdinalIgnoreCase) &&
            NikonNef.ModelName(reading.Model) is not null)
            return "NEF / JPEG";
        if (reading.Maker.StartsWith("Fujifilm", StringComparison.OrdinalIgnoreCase))
            return "RAF";
        if (reading.Maker.StartsWith("Sony", StringComparison.OrdinalIgnoreCase))
            return "ARW / JPEG";
        if (reading.Maker.StartsWith("Pentax", StringComparison.OrdinalIgnoreCase) ||
            reading.Model.StartsWith("Pentax ", StringComparison.OrdinalIgnoreCase))
            return "PEF / DNG";
        return null;
    }

    private void ShowShutterHistory()
    {
        if (_reading?.Count is not { UsbProperty: not null } count)
        {
            ShutterHistoryText.Visibility = Visibility.Collapsed;
            return;
        }

        var comparison = _shutterComparison;
        string current = FormatHistoryCount(count.Value, count.IsUpperBound);
        string message = comparison switch
        {
            null when string.IsNullOrWhiteSpace(_reading.Serial) => T("HistoryNeedsSerial"),
            null => T("NoHistory"),
            { PreviousCount: null } => T(count.IsUpperBound ? "FirstBound" : "FirstCount", current),
            { PreviousCount: uint previous } when count.Value < previous =>
                T(count.IsUpperBound ? "BoundDecreased" : "CountDecreased", FormatHistoryCount(previous, count.IsUpperBound), current),
            { PreviousCount: uint previous } when count.Value > previous =>
                T(count.IsUpperBound ? "BoundIncreased" : "CountIncreased", FormatHistoryCount(previous, count.IsUpperBound), current),
            _ => T(count.IsUpperBound ? "BoundUnchanged" : "CountUnchanged", current)
        };

        if (comparison is { PreviousCount: uint prior, LastDecrease: { } decrease } && count.Value >= prior)
            message += "\n" + T("LastDecrease", FormatHistoryCount(decrease.From, count.IsUpperBound),
                FormatHistoryCount(decrease.To, count.IsUpperBound), decrease.ObservedAt.ToLocalTime());

        ShutterHistoryText.Text = message;
        ShutterHistoryText.Foreground = (System.Windows.Media.Brush)FindResource(comparison?.LastDecrease is null ? "Secondary" : "Yellow");
        ShutterHistoryText.Visibility = Visibility.Visible;
    }

    private string FormatCount(uint count, bool isUpperBound) =>
        (isUpperBound && count > 0 ? "≤ " : "") + count.ToString("N0", _language.Culture);

    private string FormatHistoryCount(uint count, bool isUpperBound) =>
        isUpperBound && count > 0 ? T("HistoryBoundValue", count) : FormatCount(count, false);

    // Truncate to one decimal so a percentage below a boundary displays below it.
    internal string FormatPercent(double ratio) =>
        FormatLocalizedPercent((decimal)(Math.Floor(ratio * 1000) / 1000), "P1");

    // These numbers are left-to-right runs. WPF draws the Arabic letter mark as a visible box in them.
    private string FormatLocalizedPercent(decimal ratio, string format) =>
        ratio.ToString(format, _language.Culture).Replace("\u061C", "");

    private void OnApplyCount(object sender, RoutedEventArgs e)
    {
        var digits = ManualCount.Text.Trim().Select(character =>
        {
            int digit = CharUnicodeInfo.GetDecimalDigitValue(character);
            return digit < 0 ? character : (char)('0' + digit);
        });
        if (!uint.TryParse(string.Concat(digits), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            ManualError.Text = T("InvalidCount");
            ManualError.Visibility = Visibility.Visible;
            return;
        }

        _manual = new(count, CountKind.Mechanical);
        _helpOverride = null;
        ManualError.Visibility = Visibility.Collapsed;
        ShowReading();
    }

    private void OnClearCount(object sender, RoutedEventArgs e)
    {
        ResetManual();
        ShowReading();
    }

    private void ResetManual()
    {
        _manual = null;
        _helpOverride = null;
        ManualCount.Clear();
        ManualError.Visibility = Visibility.Collapsed;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        OpenPhotoButton.IsEnabled = !busy;
        DevicePicker.IsEnabled = !busy;
    }

    private void SetConnection(string key, string brush = "Secondary")
    {
        _connectionKey = key;
        ConnectionText.Text = T(key);
        ConnectionDot.Fill = (System.Windows.Media.Brush)FindResource(brush);
    }

    private void SetHelpOverride(string key, string? argument = null)
    {
        _helpOverride = (key, argument);
        HelpText.Text = T(key, argument);
    }

    private void OnScaleSizeChanged(object sender, SizeChangedEventArgs e) => UpdateMarker();

    private void UpdateMarker()
    {
        Marker.Visibility = _ratio.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (_ratio is double ratio)
            Canvas.SetLeft(Marker, Math.Clamp(ratio, 0, 1) * Math.Max(0, Scale.ActualWidth - Marker.Width));
    }

    private void OnOpenLink(object sender, RequestNavigateEventArgs e)
    {
        if (!App.TryOpenLink(e))
            SetHelpOverride("BrowserFailed", e.Uri.AbsoluteUri);
    }
}
