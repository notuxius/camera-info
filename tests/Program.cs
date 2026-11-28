using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CameraInspector;

internal static partial class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        var rating = CameraRatings.For("Canon EOS R6 Mark II") ?? throw new Exception("Missing Canon rating.");
        Check(rating.Cycles == 400_000, "R6 Mark II has its own rating");
        Check(CameraRatings.For("Canon EOS R5 C") is null, "Similar model names do not inherit ratings");
        Check(ShutterUsage.From(new(0, CountKind.Mechanical), rating)?.Zone == UsageZone.Low, "Zero is a valid low count");
        Check(ShutterUsage.From(new(199_999, CountKind.Mechanical), rating)?.Zone == UsageZone.Low, "Below 50% is low");
        Check(ShutterUsage.From(new(200_000, CountKind.Mechanical), rating)?.Zone == UsageZone.Elevated, "50% is elevated");
        Check(ShutterUsage.From(new(319_999, CountKind.Mechanical), rating)?.Zone == UsageZone.Elevated, "Below 80% is elevated");
        Check(ShutterUsage.From(new(320_000, CountKind.Mechanical), rating)?.Zone == UsageZone.High, "80% is high");
        Check(ShutterUsage.From(new(500_000, CountKind.Mechanical), rating)?.Ratio == 1.25, "Over-rating count remains visible");
        Check(ShutterUsage.From(new(5, CountKind.Unspecified), rating) is null, "Unspecified count has no zone");
        Check(ShutterUsage.From(null, rating) is null, "Missing count has no zone");
        Check(ShutterUsage.From(new(5, CountKind.Mechanical), null) is null, "Missing rating has no zone");
        CheckCanonUsb(rating);
        Task.Run(CheckCameraEmulation).GetAwaiter().GetResult();
        Task.Run(CheckNikonUsb).GetAwaiter().GetResult();
        Task.Run(CheckAdditionalUsbCameras).GetAwaiter().GetResult();

        int cameras = Task.Run(() => WindowsCamera.Find().Count).GetAwaiter().GetResult();
        Console.WriteLine($"Windows USB discovery completed with {cameras} supported camera(s).");

        var app = new App();
        app.InitializeComponent();
        var folder = Directory.CreateTempSubdirectory("camera-inspector-language-");
        try
        {
            CheckDashboard(rating, Path.Combine(folder.FullName, "language.txt"), args.Length == 1 ? args[0] : null);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
        Console.WriteLine("All camera checks passed.");
    }

    private static void CheckDashboard(CameraRating rating, string preferencePath, string? renderFolder)
    {
        var window = new MainWindow(preferencePath);
        var dashboard = (Grid)window.FindName("Dashboard");
        Check(window.FormatPercent(199_999d / rating.Cycles) == "49.9%", "Displayed percentage respects zone boundary");
        Check(Label(window, "CountText") == "—", "Disconnected window has no count");
        if (renderFolder is not null)
            Render(dashboard, renderFolder, "disconnected");
        string testDirectory = Path.GetDirectoryName(preferencePath)!;
        CheckEmulatedScan(preferencePath, Path.Combine(testDirectory, "emulated-history"));
        CheckScanWithoutCamera(preferencePath, Path.Combine(testDirectory, "empty-scan-history"));
        CheckScanWithDisconnect(preferencePath, Path.Combine(testDirectory, "disconnect-history"));
        CheckScanWithShutterReadDisconnect(preferencePath, Path.Combine(testDirectory, "read-disconnect-history"));
        CheckNikonPhotos(preferencePath, testDirectory, renderFolder);
        CheckAdditionalPhotos(preferencePath, testDirectory, renderFolder);
        CheckShutterHistory(window, renderFolder);
        if (renderFolder is not null)
            Render(dashboard, renderFolder, "shutter-decrease");
        foreach (var (count, zone) in new[] { (32_000u, "Low usage"), (87_000u, "Elevated usage"), (165_000u, "High usage") })
        {
            window.ShowUsbReading(ExampleReading(count), null);
            Check(Label(window, "ZoneText") == zone && Label(window, "CountText") == count.ToString("N0"), "Dashboard renders the correct usage status");
            Check(((TextBlock)window.FindName("ShutterHistoryText")).Visibility == Visibility.Collapsed, "A reading without a USB counter property hides history");
            if (renderFolder is not null)
                Render(dashboard, renderFolder, zone.Split(' ')[0].ToLowerInvariant());
        }
        CheckLanguages(window, preferencePath, renderFolder);
    }

    private static CameraReading ExampleReading(uint count) =>
        new("Canon EOS 5D Mark III", "Canon", "EXAMPLE", "1.3.6", 72, new(count, CountKind.Mechanical));

    private static void CheckAbout(LanguageOption option, IReadOnlyDictionary<string, string> catalog,
        FlowDirection direction, string? renderFolder)
    {
        var dialog = new AboutWindow(option, catalog);
        var supportedCameras = (Hyperlink)dialog.FindName("SupportedCamerasLink");
        var checkForUpdates = (Hyperlink)dialog.FindName("CheckForUpdatesLink");
        var close = (Button)dialog.FindName("CloseButton");
        string version = typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
        Check(dialog.Title == catalog["About"] && dialog.FlowDirection == direction &&
            Label(dialog, "VersionText") == string.Format(option.Culture, catalog["Version"], version),
            $"About shows the app version and uses the selected language ({option.Code})");
        Check(close.IsCancel && (string)close.Content == catalog["Close"], $"About has a localized Close button ({option.Code})");
        Check(supportedCameras.NavigateUri.AbsoluteUri == "https://github.com/notuxius/camera-inspector#brand-support-backlog" &&
            ((Run)supportedCameras.Inlines.FirstInline).Text == catalog["SupportedCameras"],
            $"About links to supported cameras ({option.Code})");
        Check(checkForUpdates.NavigateUri.AbsoluteUri == "https://github.com/notuxius/camera-inspector/releases/latest" &&
            ((Run)checkForUpdates.Inlines.FirstInline).Text == catalog["CheckForUpdates"],
            $"About links to the latest GitHub release ({option.Code})");
        if (renderFolder is not null && option.Code is "en" or "ar")
            Render((Grid)dialog.Content, renderFolder, $"about-{option.Code}", 344, 224);
        dialog.Close();
    }

    private static void CheckLanguages(MainWindow window, string preferencePath, string? renderFolder)
    {
        var picker = (ComboBox)window.FindName("LanguagePicker");
        var english = (LanguageOption)picker.SelectedItem;
        var original = Languages.Catalog(english);
        var aboutButton = (Button)window.FindName("AboutButton");
        Check(english.Code == "en", "No saved language defaults to English");
        Check(string.Join(" ", picker.Items.Cast<LanguageOption>().Select(option => option.Code)) ==
            "ar bn zh arz en fr de hi id ja pcm pt ru es uk ur", "Picker matches Relf's 16 languages");

        foreach (LanguageOption option in picker.Items)
        {
            window.ShowUsbReading(ExampleReading(87_000), null);
            var catalog = Languages.Catalog(option);
            Check(original.Keys.ToHashSet().SetEquals(catalog.Keys), $"{option.Code} has all messages");
            foreach (var (key, message) in catalog)
            {
                Check(!string.IsNullOrWhiteSpace(message) &&
                    CompositeFormat.Parse(message).MinimumArgumentCount == CompositeFormat.Parse(original[key]).MinimumArgumentCount,
                    $"{option.Code} has a valid {key} message");
            }

            picker.SelectedItem = option;
            var direction = option.Code is "ar" or "arz" or "ur" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            Check(window.FlowDirection == direction, $"{option.Code} has the expected layout direction");
            Check((string)((Button)window.FindName("ScanButton")).Content == catalog["Scan"], "Static labels update on selection");
            Check((string)((Button)window.FindName("OpenPhotoButton")).Content == catalog["OpenPhoto"], "The photo button updates on language selection");
            Check((string)aboutButton.Content == catalog["About"], $"The About button updates to {option.Code}");
            CheckAbout(option, catalog, direction, renderFolder);
            Check(Label(window, "ConnectionText") == catalog["UsbConnected"] &&
                Label(window, "ZoneText") == catalog["ElevatedUsage"], "Switching language preserves and retranslates the USB reading");
            Check(Label(window, "CountText") == 87_000.ToString("N0", option.Culture), "Shutter count uses the selected language's number format");
            if (renderFolder is not null)
                Render((Grid)window.FindName("Dashboard"), renderFolder, $"language-{option.Code}", 786);
        }

        var restored = new MainWindow(preferencePath);
        Check(((LanguageOption)((ComboBox)restored.FindName("LanguagePicker")).SelectedItem).Code == "ur" &&
            restored.FlowDirection == FlowDirection.RightToLeft, "A new window restores the saved language");
        restored.Close();

        var reading = new CameraReading("Canon EOS R7", "Canon", "12345", "1.3.6", 72,
            new(100_000, CountKind.Mechanical, 0xD167, IsUpperBound: true));
        if (renderFolder is not null)
        {
            picker.SelectedItem = Languages.Options.Single(option => option.Code == "ar");
            window.ShowUsbReading(reading, new(100_000, new(101_000, 100_000, DateTimeOffset.Now)));
            Render((Grid)window.FindName("Dashboard"), renderFolder, "rounded-ar", 786, 880);
        }
        window.ShowUsbReading(reading, new(101_000, new(101_000, 100_000, DateTimeOffset.Now)));
        var manualCount = (TextBox)window.FindName("ManualCount");
        var applyCount = (Button)window.FindName("ApplyCountButton");
        manualCount.Text = "abc";
        applyCount.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        picker.SelectedItem = Languages.Options.Single(option => option.Code == "bn");
        Check(Label(window, "ManualError") == Languages.Catalog((LanguageOption)picker.SelectedItem)["InvalidCount"], "A visible error is retranslated");
        manualCount.Text = "৯৯৫০০";
        applyCount.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        picker.SelectedItem = english;
        Check(Label(window, "CountText") == "99,500" && Label(window, "SourceValue") == "Manual count" &&
            Label(window, "ShutterHistoryText") == "USB shutter count bound decreased from ≤ 101,000 to ≤ 100,000.",
            "A localized manual count and USB history survive switching languages");

        File.WriteAllText(preferencePath, "unknown");
        Check(Languages.ReadPreference(preferencePath) == english, "An unknown saved language falls back to English");
    }

    private static void CheckCanonUsb(CameraRating rating)
    {
        Check(CanonShutter.UsbModel("VID_0000&PID_3281") is null, "Other vendors cannot select the Canon monitor");
        Check(CanonShutter.UsbModel("USB\\VID_04A9&PID_3294") == "Canon EOS 80D", "The 80D diagnostic route uses its recorded USB product ID");
        Check(CanonShutter.UsbModel("VID_04A9&PID_3320") == "Canon EOS R50 V" &&
            CanonShutter.ProtocolFor("Canon EOS R50 V") == CanonCounterProtocol.Unknown,
            "The EOS R50 V has its own identity and does not inherit the EOS R50 counter format");
        foreach (string name in new[] { "Canon EOS M50m2", "Canon EOS Kiss M2", "Canon EOS Kiss X10i", "Canon EOS Rebel T8i", "Canon EOS 5Rm2", "Canon EOS R6m2" })
            Check(CanonShutter.ProtocolFor(name) == CanonCounterProtocol.ReleaseEvent, $"Firmware and regional name {name} is recognized");

        byte[] records = new byte[84];
        WriteRecord(records, CanonShutter.LegacyProperty, 0);
        WriteRecord(records.AsSpan(16), CanonShutter.ReleaseProperty, 87_000);
        WriteRecord(records.AsSpan(32), CanonShutter.LegacyProperty, uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(48), 28);
        BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(52), 0xC189);
        BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(56), CanonShutter.ReleaseProperty);
        // Canon EOS R capture from libgphoto2: 4,000 mechanical and 35,000 total, rounded up.
        Convert.FromHexString("1000000001000000a00f0000b8880000").CopyTo(records, 60);
        BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(76), uint.MaxValue);

        var legacy = new CanonShutterEvents(CanonCounterProtocol.Legacy);
        legacy.Parse(records);
        Check(legacy.Result is { Value: 0, Kind: CountKind.Mechanical, HasRange: false }, "Legacy zero stays valid");
        var modern = new CanonShutterEvents(CanonCounterProtocol.ReleaseEvent);
        modern.Parse(records);
        Check(modern.Result is { Value: 4_000, LowerBound: 3_001, Kind: CountKind.Mechanical }, "Mechanical release bound takes precedence over scalar fallbacks");
        var unknown = new CanonShutterEvents(CanonCounterProtocol.Unknown);
        unknown.Parse(records);
        Check(unknown.Result is { Kind: CountKind.Unspecified }, "A structured value for an unlisted body is not labeled mechanical");
        var scalarOnly = new CanonShutterEvents(CanonCounterProtocol.ReleaseEvent);
        scalarOnly.Parse(records.AsSpan(16, 16));
        Check(scalarOnly.Result is { Value: 87_000, Kind: CountKind.Unspecified }, "A legacy scalar release stays ambiguous on a modern body");
        Span<byte> highLegacyRecord = stackalloc byte[16];
        WriteRecord(highLegacyRecord, CanonShutter.LegacyProperty, 301_123);
        legacy.Parse(highLegacyRecord);
        Check(legacy.Result is { Value: 301_123, Kind: CountKind.Mechanical }, "Legacy counts retain their full width");
        Check(CanonShutter.DecodeRelease(Convert.FromHexString("100000000100000090D0030060AE0A00")) is
            { Value: 250_000, LowerBound: 249_001, Kind: CountKind.Mechanical }, "Modern release events preserve high mechanical bounds");
        foreach (string hex in new[] { "1000000002000000A00F0000B8880000", "1000000001000000A10F0000B8880000", "1000000001000000B8880000A00F0000", "1000000001000000A00F0000" })
            Check(CanonShutter.DecodeRelease(Convert.FromHexString(hex)) is null, "Reject an unfamiliar or invalid release payload");
        Check(CanonShutter.DecodeRelease(Convert.FromHexString("10000000010000000000000000000000")) is { Value: 0, HasRange: false }, "A zero bound represents zero");

        Check(Convert.ToHexStringLower(CanonShutter.MonitorShutterPayload()) ==
            "4d6f6e52656164416e6447657444617461000300000002000000020000000000000000000000000000000200000001100000000000000000000000000000020000000a00000000000000000000000000000000000000",
            "The fixed monitor request matches the captured packet");
        Check(CanonShutter.MonitorSessionPayload(open: true).AsSpan(0, 8).SequenceEqual("MonOpen\0"u8) &&
            CanonShutter.MonitorSessionPayload(open: false).AsSpan(0, 9).SequenceEqual("MonClose\0"u8), "Monitor session uses matching open and close payloads");
        Check(CanonShutter.DecodeMonitorShutter(Convert.FromHexString("0066000000000000F200")) is { Value: 26_354 }, "5D Mark IV shutter sample");
        Check(CanonShutter.DecodeMonitorShutter(Convert.FromHexString("00670000000000000006")) is { Value: 26_374 }, "5D Mark IV sample after 20 actuations");
        Check(CanonShutter.DecodeMonitorShutter(Convert.FromHexString("00050000000000B90000")) is { Value: 1_465 }, "6D Mark II shutter sample");
        Check(CanonShutter.DecodeMonitorShutter(Convert.FromHexString("00050000000000D20000")) is { Value: 1_490 }, "6D Mark II sample after 25 actuations");
        // EOS 5DS R diagnostic captures from openshuttercounter pull request #5.
        Check(CanonShutter.DecodeMonitorShutter(Convert.FromHexString("02E200007C0000000000")) is { Value: 189_052, Kind: CountKind.Mechanical }, "High-count shutter sample preserves both high bytes");
        Check(CanonShutter.DecodeMonitorShutter(Convert.FromHexString("02E20000900000000000")) is { Value: 189_072 }, "High-count shutter sample advances by 20 actuations");
        Check(CanonShutter.DecodeMonitorShutter(Convert.FromHexString("00FF00000000000000FF")) is { Value: 65_535 } &&
            CanonShutter.DecodeMonitorShutter(Convert.FromHexString("01000000000000000000")) is { Value: 65_536 }, "Diagnostic decoder crosses the 16-bit boundary");
        Check(CanonShutter.DecodeMonitorShutter(Convert.FromHexString("00050000000000D20100")) is null, "Ambiguous monitor values stay unavailable");

        Check(ShutterUsage.From(new(199_000, CountKind.Mechanical, IsUpperBound: true), rating) is { Zone: UsageZone.Low }, "An interval within one wear zone is usable");
        Check(ShutterUsage.From(new(200_000, CountKind.Mechanical, IsUpperBound: true), rating) is null, "An interval crossing a threshold does not pick a wear zone");
    }

    private static void CheckShutterHistory(MainWindow window, string? renderFolder)
    {
        var folder = Directory.CreateTempSubdirectory("camera-inspector-history-");
        try
        {
            var reading = new CameraReading("Canon EOS R7", "Canon", "12345", null, null, new(42_000, CountKind.Unspecified, 0xD167));
            Check(ShutterHistory.TryRecord(reading with { Serial = null }, folder.FullName) is null, "Missing serial cannot establish a baseline");
            Check(ShutterHistory.TryRecord(reading with { Count = new(12, CountKind.Mechanical) }, folder.FullName) is null, "Manual count cannot establish a baseline");
            Check(ShutterHistory.TryRecord(reading, folder.FullName) is { PreviousCount: null, LastDecrease: null }, "First scan saves a baseline");

            var increased = reading with { Count = new(42_001, CountKind.Unspecified, 0xD167) };
            Check(ShutterHistory.TryRecord(increased, folder.FullName) is { PreviousCount: 42_000, LastDecrease: null }, "Increase compares against the last scan");
            Check(ShutterHistory.TryRecord(increased, folder.FullName) is { PreviousCount: 42_001, LastDecrease: null }, "An unchanged count is retained");
            Check(ShutterHistory.TryRecord(reading with { Serial = "67890" }, folder.FullName) is { PreviousCount: null }, "Another body has its own baseline");
            Check(ShutterHistory.TryRecord(reading with { Count = new(0, CountKind.Unspecified, 0xD1AC) }, folder.FullName) is { PreviousCount: null, LastDecrease: null }, "Different USB counters for one body have independent baselines");
            var boundReading = reading with { Count = new(100_000, CountKind.Mechanical, 0xD167, IsUpperBound: true) };
            var boundHistory = ShutterHistory.TryRecord(boundReading, folder.FullName);
            Check(boundHistory is { PreviousCount: null, LastDecrease: null }, "A mechanical bound starts a new baseline alongside the old scalar counter");
            window.ShowUsbReading(boundReading, boundHistory);
            Check(Label(window, "CountText") == "≤ 100,000" && Label(window, "UsageNote").Contains("99,001–100,000"), "Dashboard displays the reported interval");
            Check(Label(window, "ZoneText") == "Usage unavailable" && Label(window, "ShutterHistoryText").Contains("bound of ≤ 100,000"), "Wear and history respect the interval");
            Check(((StackPanel)window.FindName("ManualPanel")).Visibility == Visibility.Visible, "An exact manual reading is still available for a rounded count");
            if (renderFolder is not null)
                Render((Grid)window.FindName("Dashboard"), renderFolder, "rounded-count");
            window.ShowUsbReading(boundReading with { Count = new(101_000, CountKind.Mechanical, 0xD167, IsUpperBound: true) }, null);
            Check(Label(window, "PercentText") == "≤ 50.5%" && Label(window, "ZoneText") == "Elevated usage", "A range inside one wear zone shows the upper bound");

            var decreased = reading with { Count = new(0, CountKind.Unspecified, 0xD167) };
            var drop = ShutterHistory.TryRecord(decreased, folder.FullName);
            Check(drop is { PreviousCount: 42_001, LastDecrease: { From: 42_001, To: 0 } }, "A lower reading records the drop");
            var recovered = ShutterHistory.TryRecord(increased, folder.FullName);
            Check(recovered is { PreviousCount: 0 } && recovered.Value.LastDecrease == drop?.LastDecrease, "The most recent drop survives an increase");

            window.ShowUsbReading(increased, recovered);
            Check(Label(window, "ShutterHistoryText").Contains("Last observed decrease was from 42,001 to 0"), "A past decrease remains visible");
            window.ShowUsbReading(decreased, drop);
            Check(Label(window, "ShutterHistoryText") == "USB shutter count decreased from 42,001 to 0.", "A decrease shows the previous and current counts");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static string Label(Window window, string name) => ((TextBlock)window.FindName(name)).Text;

    private static void WriteRecord(Span<byte> bytes, uint property, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], 0xC189);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], property);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], value);
    }

    private static void Check(bool passed, string label)
    {
        if (!passed)
            throw new Exception(label);
    }

    private static void Render(Grid dashboard, string folder, string name, double width = 1024, double height = 720)
    {
        Directory.CreateDirectory(folder);
        var size = new Size(width, height);
        dashboard.Measure(size);
        dashboard.Arrange(new Rect(size));
        dashboard.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dashboard);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }
}
