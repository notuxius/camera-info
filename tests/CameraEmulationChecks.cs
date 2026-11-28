using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CameraInspector;

internal static partial class Program
{
    private const uint PtpRejected = 0x2002;
    private const int DeviceDisconnected = unchecked((int)0x8007048F);
    private const int DeviceIoError = unchecked((int)0x8007045D);

    private static void CheckCameraEmulation()
    {
        CheckEmulatedRoutes();

        var readOnly = new EmulatedCamera(Camera("323A")) { ReadOnly = true, Battery = 101 }.Read();
        Check(readOnly is { Model: "Canon EOS 5D Mark III", Serial: "EMULATED-123", Battery: null, Count: null },
            "Read-only fallback preserves metadata, rejects invalid battery levels, and skips shutter commands");

        var withoutMetadata = new EmulatedCamera(Camera("323A"), Read(0x1015, Counter(0), 0xD1AC),
            Read(0x1004, Counter(0))) { MissingDetails = true }.Read();
        Check(withoutMetadata is { Model: "Canon EOS 5D Mark III", Maker: "Canon", Serial: null, Count: { Value: 0, Kind: CountKind.Mechanical } },
            "A known USB ID can read an exact zero without device metadata");

        var compact = new EmulatedCamera(Camera("FFFF")) { Model = " Canon PowerShot G7 X " }.Read();
        Check(compact is { Model: "Canon PowerShot G7 X", Count: null }, "A PowerShot reports metadata without EOS commands");

        var unknownId = new EmulatedCamera(Camera("FFFF"), Read(0x1015, Counter(99), 0xD1AC)) { Model = "Canon EOS 80D" }.Read();
        Check(unknownId.Count == new ShutterCount(99, CountKind.Unspecified, 0xD1AC),
            "A name alone cannot enable diagnostic commands or make a legacy fallback mechanical");

        CheckEmulatedFallbacks();
        Console.WriteLine("Emulated Canon camera checks passed for all 24 USB model routes and failure scenarios.");
    }

    private static void CheckEmulatedRoutes()
    {
        foreach (var (id, model) in new[]
        {
            ("3199", "5D Mark II"), ("319A", "7D"), ("323A", "5D Mark III"),
            ("3250", "6D"), ("3253", "70D"), ("326F", "7D Mark II")
        })
        {
            var reading = new EmulatedCamera(Camera(id), Read(0x1015, Counter(301_123), 0xD1AC) with { ChunkSize = 2, ExpectedReadCalls = 2 }).Read();
            Check(reading.Model == $"Canon EOS {model}" && reading.Count == new ShutterCount(301_123, CountKind.Mechanical, 0xD1AC),
                $"{model} reads a full-width legacy count across two chunks");
            Check(reading is { Maker: "Canon", Serial: "EMULATED-123", Firmware: "1.3.6", Battery: 72 }, $"{model} reads WPD metadata");
        }

        foreach (var (id, model) in new[] { ("3281", "5D Mark IV"), ("3294", "80D"), ("32CA", "6D Mark II") })
        {
            var reading = new EmulatedCamera(Camera(id), MonitorOpen(), MonitorQuery(),
                Read(0x9053, Convert.FromHexString("02E200007C0000000000"), 0, 0, 1) with { AnnouncedSize = uint.MaxValue },
                MonitorClose()).Read();
            Check(reading.Model == $"Canon EOS {model}" && reading.Count == new ShutterCount(189_052, CountKind.Mechanical, 0x1001),
                $"{model} reads a full-width diagnostic count with an unknown transfer size");
        }

        foreach (var (id, model) in new[]
        {
            ("32DA", "R"), ("32E2", "RP"), ("32EA", "90D"), ("32F1", "850D"), ("32F4", "R5"),
            ("32F5", "R6"), ("32F7", "R7"), ("32F8", "R10"), ("32F9", "M50 Mark II"), ("330B", "R6 Mark II"),
            ("330C", "R8"), ("330D", "R50"), ("3312", "R100"), ("3314", "R5 Mark II"), ("3323", "R6 Mark III")
        })
        {
            var reading = EmulateRelease(id, 250_000);
            Check(reading.Model == $"Canon EOS {model}" && reading.Count == new ShutterCount(250_000, CountKind.Mechanical, 0xD167, IsUpperBound: true),
                $"{model} prefers the mechanical release bound over the legacy counter");
        }
    }

    private static void CheckEmulatedFallbacks()
    {
        byte[] invalidRelease = ReleaseEvent(4_000);
        BinaryPrimitives.WriteUInt32LittleEndian(invalidRelease.AsSpan(16), 2);
        byte[] splitEvent = new byte[16_380 + 28];
        BinaryPrimitives.WriteUInt32LittleEndian(splitEvent, 16_380);
        BinaryPrimitives.WriteUInt32LittleEndian(splitEvent.AsSpan(4), 0xC18A);
        ReleaseEvent(4_000).CopyTo(splitEvent, 16_380);
        var requested = new EmulatedCamera(Camera("32F1"), Read(0x1015, Counter(9_000), 0xD1AC),
            Control(0x9114, 1), Control(0x9115, 1), Read(0x9108, []), Read(0x9116, invalidRelease),
            Read(0x9116, []) with { Response = PtpRejected }, Control(0x9127, 0xD167), Read(0x9116, splitEvent) with { ExpectedReadCalls = 2 },
            Control(0x9115, 0), Control(0x9114, 0)) { Model = "Canon EOS Rebel T8i" }.Read();
        Check(requested is { Model: "Canon EOS Rebel T8i", Count: { Value: 4_000, LowerBound: 3_001, Kind: CountKind.Mechanical } },
            "A release event obtained by request crosses WPD chunks and preserves the camera's regional name");

        var shortWrite = new EmulatedCamera(Camera("3294"), MonitorOpen(), MonitorQuery() with { ChunkSize = 1 },
            MonitorClose(), Read(0x1015, Counter(88_000), 0xD1AC)).Read();
        Check(shortWrite.Count == new ShutterCount(88_000, CountKind.Unspecified, 0xD1AC),
            "A short diagnostic write ends the session and falls back to an uncertain counter");

        byte[] ambiguousReply = Convert.FromHexString("00050000000000D20100");
        var scalar = new byte[16];
        WriteRecord(scalar, 0xD167, 87_000);
        var invalidMonitor = new EmulatedCamera(Camera("3294"), MonitorOpen(), MonitorQuery(), Read(0x9053, ambiguousReply, 0, 0, 1),
            MonitorClose(), Read(0x1015, Counter(uint.MaxValue), 0xD1AC), Control(0x9114, 1), Control(0x9115, 1), Read(0x9108, []),
            Read(0x9116, scalar), Read(0x9116, []) with { AnnouncedSize = 16, ReadError = DeviceIoError }, Control(0x9127, 0xD1AC, PtpRejected),
            Control(0x9115, 0), Control(0x9114, 0)).Read();
        Check(invalidMonitor.Count == new ShutterCount(87_000, CountKind.Unspecified, 0xD167),
            "An ambiguous diagnostic reply falls back to a scalar event and closes both modes after a read error");

        CameraExchange legacy = Read(0x1015, Counter(100), 0xD1AC);
        foreach (var (name, failedRead) in new[]
        {
            ("truncated transfer", legacy with { AnnouncedSize = 5, ExpectedReadCalls = 2 }),
            ("oversized transfer", legacy with { AnnouncedSize = 1_048_577, ExpectedReadCalls = 0 }),
            ("failed PTP response", legacy with { Response = PtpRejected }),
            ("driver I/O error", legacy with { ReadError = DeviceIoError, ExpectedReadCalls = 1 }),
            ("sentinel counter", legacy with { Data = Counter(uint.MaxValue) })
        })
        {
            var reading = new EmulatedCamera(Camera("323A"), failedRead, Control(0x9114, 1),
                Control(0x9115, 1, PtpRejected), Control(0x9114, 0)).Read();
            Check(reading.Count is null, $"A {name} stays unavailable and closes the remote mode");
        }

        var eventCounter = new byte[16];
        WriteRecord(eventCounter, 0xD1AC, 301_123);
        var legacyEvent = new EmulatedCamera(Camera("323A"), legacy with { Response = PtpRejected },
            Control(0x9114, 1, PtpRejected), Control(0x9115, 1), Read(0x9108, []), Read(0x9116, eventCounter), Control(0x9115, 0)).Read();
        Check(legacyEvent.Count == new ShutterCount(301_123, CountKind.Mechanical, 0xD1AC),
            "A legacy body can read its counter from events even when remote mode is refused");
    }

    private static void CheckEmulatedScan(string preferencePath, string historyDirectory)
    {
        var camera = Camera("32F7");
        var manager = new EmulatedDeviceManager(new(@"\\?\usb#vid_1234&pid_5678#emulated", "Portable music player"), camera);
        var reads = new Queue<EmulatedCamera>([
            ReleaseCamera(camera, 101_000, 0x1E, battery: 20), ReleaseCamera(camera, 100_000, 0)
        ]);
        var window = new MainWindow(preferencePath, historyDirectory, () => WindowsCamera.Find(manager), device => reads.Dequeue().Read(device));

        RunScanCheck(window, async () =>
        {
            var picker = (ComboBox)window.FindName("DevicePicker");
            Check(manager.Scans == 0 && picker.Items.Count == 0 && Label(window, "CountText") == "—",
                "The off-screen window starts before discovery");

            await ClickScanAsync(window);
            Check(manager.Scans == 1 && picker.Items.Count == 1 && Equals(picker.SelectedItem, camera) && reads.Count == 1,
                "The scan discovers and selects the Canon camera and filters out the unrelated device");
            Check(Label(window, "ModelTitle") == "Canon EOS R7" && Label(window, "SerialValue") == "EMULATED-123" &&
                Label(window, "FirmwareValue") == "1.3.6" && Label(window, "BatteryValue") == "20%" &&
                Label(window, "ConnectionText") == "Connected over USB" && Label(window, "CountText") == "≤ 101,000" &&
                Label(window, "ZoneText") == "Elevated usage" && Label(window, "ShutterHistoryText").Contains("bound of ≤ 101,000"),
                "The scan displays USB status, metadata, mechanical bound, usage, and a history baseline");
            Check(Label(window, "CameraErrorText") == "Possible Err 30 — shutter malfunction (0x0000001E)." &&
                Label(window, "CameraErrorActionText") ==
                    "Confirm the code on the camera display first. Turn the camera off and back on. If the error returns, note the code for Canon service." &&
                Label(window, "CameraWarningsText") == "Low battery (20%).\nRecharge the battery or insert a charged spare.",
                "The scan explains the camera error alongside the low battery warning");

            await ClickScanAsync(window);
            Check(manager.Scans == 2 && Equals(picker.SelectedItem, camera) && reads.Count == 0 &&
                Label(window, "ConnectionText") == "Connected over USB" && Label(window, "CountText") == "≤ 100,000" &&
                Label(window, "ZoneText") == "Usage unavailable" &&
                Label(window, "ShutterHistoryText") == "USB shutter count bound decreased from ≤ 101,000 to ≤ 100,000.",
                "A rescan discovers the camera again and updates the dashboard and saved history");
            Check(Label(window, "CameraErrorText") == "The camera reported no active error." &&
                ((TextBlock)window.FindName("CameraErrorActionText")).Visibility == Visibility.Collapsed &&
                Label(window, "CameraWarningsText") ==
                    "A decrease was recorded in the USB shutter count.\nCompare with an earlier reading or service report for this camera.",
                "A rescan clears the resolved warnings and shows the recorded shutter count decrease");
        });
        Console.WriteLine("End-to-end scan and rescan with an emulated camera passed.");
    }

    private static void CheckScanWithoutCamera(string preferencePath, string historyDirectory)
    {
        var manager = new EmulatedDeviceManager();
        var window = new MainWindow(preferencePath, historyDirectory, () => WindowsCamera.Find(manager),
            _ => throw new InvalidOperationException("An empty scan attempted to open a camera."));

        RunScanCheck(window, async () =>
        {
            window.ShowUsbReading(ExampleReading(87_000), null);
            await ClickScanAsync(window);
            Check(manager.Scans == 1, "The scan runs discovery");
            CheckNoCameraFound(window);

            var dialog = window.CreateSettingsWindow();
            var picker = (ComboBox)dialog.FindName("LanguagePicker");
            var original = picker.SelectedItem;
            foreach (LanguageOption option in picker.Items)
            {
                picker.SelectedItem = option;
                Check(Label(window, "ConnectionText") == Languages.Catalog(Languages.Resolve(option))["NoCameraDetected"],
                    $"An empty scan uses the generic message in {option.Code}");
            }
            picker.SelectedItem = original;
            dialog.Close();
        });
        Console.WriteLine("End-to-end scan with no camera found passed.");
    }

    private static void CheckScanWithDisconnect(string preferencePath, string historyDirectory)
    {
        var camera = Camera("32F7");
        var manager = new EmulatedDeviceManager(camera);
        var unplugged = new EmulatedCamera(camera)
        {
            BeforeOpen = () =>
            {
                manager.Devices = [];
                throw new COMException("The emulated camera disconnected after discovery.", DeviceDisconnected);
            }
        };
        var reads = new Queue<EmulatedCamera>([ReleaseCamera(camera, 101_000), unplugged, ReleaseCamera(camera, 102_000)]);
        var window = new MainWindow(preferencePath, historyDirectory, () => WindowsCamera.Find(manager), device => reads.Dequeue().Read(device));

        RunScanCheck(window, async () =>
        {
            await ClickScanAsync(window);
            Check(Label(window, "CountText") == "≤ 101,000" && Label(window, "ShutterHistoryText").Contains("bound of ≤ 101,000"),
                "A successful scan displays a reading and saves the history baseline");

            await ClickScanAsync(window);
            var picker = (ComboBox)window.FindName("DevicePicker");
            Check(manager.Scans == 2 && Equals(picker.SelectedItem, camera) && manager.Devices.Length == 0 && reads.Count == 1,
                "Discovery finds the camera before it disconnects during the open attempt");
            Check(Label(window, "ConnectionText") == "Camera could not be opened" &&
                Label(window, "HelpText") == "Wake the camera, close other camera apps, then scan again.",
                "The dashboard shows opening failure and retry guidance");
            CheckClearedScanDashboard(window);

            await ClickScanAsync(window);
            Check(manager.Scans == 3 && reads.Count == 1, "A scan while unplugged makes no new open attempt");
            CheckNoCameraFound(window);

            manager.Devices = [camera];
            await ClickScanAsync(window);
            Check(manager.Scans == 4 && Equals(picker.SelectedItem, camera) && reads.Count == 0 &&
                Label(window, "ConnectionText") == "Connected over USB" && Label(window, "CountText") == "≤ 102,000" &&
                Label(window, "ShutterHistoryText") == "USB shutter count bound increased from ≤ 101,000 to ≤ 102,000.",
                "A scan after reconnecting recovers the dashboard and compares against the last successful reading");
        });
        Console.WriteLine("End-to-end scan with a camera disconnect and reconnect passed.");
    }

    private static void CheckScanWithShutterReadDisconnect(string preferencePath, string historyDirectory)
    {
        var camera = Camera("32F7");
        var manager = new EmulatedDeviceManager(camera);
        var interruptedEvent = Read(0x9116, ReleaseEvent(102_000)) with
        {
            ChunkSize = 24,
            ReadErrorAfterBytes = 24,
            ReadError = DeviceDisconnected,
            EndError = unchecked((int)0x80004005),
            ExpectedReadCalls = 2,
            DisconnectOnReadError = () => manager.Devices = []
        };
        var unplugged = new EmulatedCamera(camera, Read(0x1015, Counter(uint.MaxValue), 0xD1AC),
            Control(0x9114, 1), Control(0x9115, 1), Read(0x9108, []), interruptedEvent);
        var reads = new Queue<EmulatedCamera>([ReleaseCamera(camera, 101_000), unplugged, ReleaseCamera(camera, 103_000)]);
        var window = new MainWindow(preferencePath, historyDirectory, () => WindowsCamera.Find(manager), device => reads.Dequeue().Read(device));

        RunScanCheck(window, async () =>
        {
            await ClickScanAsync(window);
            Check(Label(window, "CountText") == "≤ 101,000" && Label(window, "ShutterHistoryText").Contains("bound of ≤ 101,000"),
                "A successful scan sets the baseline before the interrupted transfer");

            await ClickScanAsync(window);
            Check(manager.Scans == 2 && manager.Devices.Length == 0 && reads.Count == 1,
                "The camera disconnects during the shutter transfer without another discovery call");
            var picker = (ComboBox)window.FindName("DevicePicker");
            Check(Label(window, "ConnectionText") == "Camera disconnected",
                "The dashboard reports a disconnect during the shutter transfer before rescanning");
            Check(picker.Items.Count == 0 && picker.SelectedItem is null && picker.Visibility == Visibility.Collapsed,
                "The unplugged camera disappears from the picker immediately");
            CheckClearedScanDashboard(window);

            await ClickScanAsync(window);
            Check(manager.Scans == 3 && reads.Count == 1, "Discovery skips opening a camera that disconnected during the transfer");
            CheckNoCameraFound(window);

            manager.Devices = [camera];
            await ClickScanAsync(window);
            Check(manager.Scans == 4 && reads.Count == 0 && Label(window, "ConnectionText") == "Connected over USB" &&
                Label(window, "CountText") == "≤ 103,000" &&
                Label(window, "ShutterHistoryText") == "USB shutter count bound increased from ≤ 101,000 to ≤ 103,000.",
                "After reconnecting, history compares against the last complete shutter reading");
        });
        Console.WriteLine("End-to-end scan with a disconnect during the shutter-count transfer passed.");
    }

    private static void CheckNoCameraFound(MainWindow window)
    {
        var picker = (ComboBox)window.FindName("DevicePicker");
        Check(picker.Items.Count == 0 && picker.SelectedItem is null && picker.Visibility == Visibility.Collapsed &&
            Label(window, "ConnectionText") == "No camera detected" &&
            Label(window, "HelpText") == "Check that the camera is on and connected with a USB data cable. " +
                "If prompted, choose photo transfer or PC connection mode. Select “Scan for cameras” again. " +
                "If the camera still isn’t found, select “Open original photo”. " +
                "Choose an original Nikon NEF or JPEG, Fujifilm RAF, Sony ARW or JPEG, or Pentax PEF or DNG.",
            "An empty discovery clears the device picker and shows connection guidance");
        CheckClearedScanDashboard(window);
    }

    private static void CheckClearedScanDashboard(MainWindow window)
    {
        Check(Label(window, "ModelTitle") == "No camera connected" && Label(window, "SerialValue") == "—" &&
            Label(window, "FirmwareValue") == "—" && Label(window, "BatteryValue") == "—" && Label(window, "CountText") == "—" &&
            Label(window, "ZoneText") == "Awaiting camera" && Label(window, "PercentText") == "",
            "The dashboard clears the prior metadata, count and usage");
        Check(((TextBlock)window.FindName("ShutterHistoryText")).Visibility == Visibility.Collapsed &&
            ((Border)window.FindName("CameraIssuesPanel")).Visibility == Visibility.Collapsed &&
            ((StackPanel)window.FindName("ManualPanel")).Visibility == Visibility.Collapsed &&
            ((Button)window.FindName("ScanButton")).IsEnabled,
            "The dashboard hides issues, history, and manual entry and re-enables scanning");
    }

    private static void RunScanCheck(MainWindow window, Func<Task> checkScan)
    {
        var frame = new DispatcherFrame();
        var check = window.Dispatcher.InvokeAsync(async () =>
        {
            try { await checkScan(); }
            finally
            {
                frame.Continue = false;
            }
        });
        try
        {
            Dispatcher.PushFrame(frame);
            check.Task.Unwrap().GetAwaiter().GetResult();
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task ClickScanAsync(MainWindow window)
    {
        var scan = (Button)window.FindName("ScanButton");
        Check(scan.IsEnabled && (string)scan.Content == "Scan for cameras", "Scan for cameras is available");
        scan.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        long deadline = Environment.TickCount64 + 10_000;
        while (!scan.IsEnabled && Environment.TickCount64 < deadline)
            await Task.Delay(10);
        Check(scan.IsEnabled, "Camera scan finishes within ten seconds");
    }

    private static CameraReading EmulateRelease(string id, uint count) => ReleaseCamera(Camera(id), count).Read();

    private static EmulatedCamera ReleaseCamera(CameraDevice camera, uint count, uint? errorCode = null, uint battery = 72) => new(camera,
        Read(0x1015, Counter(9_000), 0xD1AC), Control(0x9114, 1), Control(0x9115, 1), Read(0x9108, []),
        Read(0x9116, ReleaseEvent(count, errorCode)), Control(0x9115, 0), Control(0x9114, 0)) { Battery = battery };

    private static CameraDevice Camera(string id) => new($@"\\?\usb#vid_04a9&pid_{id}#emulated", "Canon Digital Camera");

    private static CameraExchange Control(uint opcode, uint argument, uint response = 0x2001) =>
        new(12, opcode, [argument], []) { Response = response };

    private static CameraExchange Read(uint opcode, byte[] data, params uint[] arguments) => new(13, opcode, arguments, data);

    private static CameraExchange MonitorOpen() => new(14, 0x9052, [0, 0], SessionPayload("MonOpen\0"u8));
    private static CameraExchange MonitorQuery() => new(14, 0x9052, [0, 1], CanonShutter.MonitorShutterPayload());
    private static CameraExchange MonitorClose() => new(14, 0x9052, [0, 0], SessionPayload("MonClose\0"u8));

    private static byte[] SessionPayload(ReadOnlySpan<byte> name)
    {
        var data = new byte[name.Length + 28];
        name.CopyTo(data);
        return data;
    }

    private static byte[] Counter(uint count)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, count);
        return data;
    }

    private static byte[] ReleaseEvent(uint count, uint? errorCode = null)
    {
        var data = new byte[errorCode.HasValue ? 44 : 28];
        WriteRecord(data, 0xD167, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(data, 28);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), count);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), count + 31_000);
        if (errorCode is uint code)
            WriteRecord(data.AsSpan(28), 0xD1C4, code);
        return data;
    }

    private sealed class EmulatedDeviceManager(params CameraDevice[] devices) : IPortableDeviceManager
    {
        internal CameraDevice[] Devices { get; set; } = devices;
        internal int Scans { get; private set; }
        internal Dictionary<string, uint> DeviceTypes { get; } = [];

        public void RefreshDeviceList() => Scans++;

        public void GetDevices(IntPtr ids, ref uint count)
        {
            if (ids != IntPtr.Zero)
            {
                Check(count == Devices.Length, "Discovery allocates a pointer for each emulated device");
                for (int i = 0; i < Devices.Length; i++)
                    Marshal.WriteIntPtr(ids, i * IntPtr.Size, Marshal.StringToCoTaskMemUni(Devices[i].Id));
            }
            count = (uint)Devices.Length;
        }

        public int GetDeviceFriendlyName(string id, StringBuilder? name, ref uint length)
        {
            string friendlyName = Devices.Single(device => device.Id == id).Name ?? "";
            if (name is not null)
            {
                Check(length >= friendlyName.Length + 1, "Discovery allocates room for the friendly name");
                name.Append(friendlyName);
            }
            length = (uint)friendlyName.Length + 1;
            return 0;
        }

        public void GetDeviceDescription() => throw new NotSupportedException();
        public void GetDeviceManufacturer() => throw new NotSupportedException();

        public int GetDeviceProperty(string id, string property, ref uint value, ref uint size, out uint type)
        {
            Check(property == "PORTABLE_DEVICE_TYPE" && size == sizeof(uint), "Discovery requests the WPD device type");
            type = 4;
            return DeviceTypes.TryGetValue(id, out value) ? 0 : unchecked((int)0x80070002);
        }
    }
}
