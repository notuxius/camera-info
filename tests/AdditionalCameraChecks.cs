using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using CameraInspector;

internal static partial class Program
{
    private static void CheckCompanionApps()
    {
        (string Maker, string Model, string? Firmware, string App, bool Apple, bool Google)[] matches =
        [
            ("FUJIFILM", "Fujifilm X-E5", null, "FUJIFILM XApp", true, true),
            ("Fujifilm", "X-E3", null, "FUJIFILM Camera Remote", true, true),
            ("Fujifilm", "X-HF1", null, "X half", true, true),
            ("Canon Inc.", "Canon EOS Kiss X10i", null, "Canon Camera Connect", true, true),
            ("Nikon Corporation", "Z5_2C", null, "SnapBridge", true, true),
            ("Nikon", "Nikon D750", null, "SnapBridge", false, true),
            ("Sony", "α7 IV", "Ver. 1.10", "Imaging Edge Mobile", true, true),
            ("Sony", "ILCE-7M4", "2.00", "Creators’ App", true, true),
            ("Panasonic", "DC-S1M2", null, "LUMIX Lab", true, true),
            ("Panasonic", "LUMIX S5 II", "3.0", "LUMIX Sync", true, true),
            ("Panasonic", "DC-S5M2", "3.1", "LUMIX Lab", true, true),
            ("OM Digital Solutions Corporation", "Olympus OM-D E-M1MarkIII", null, "OM Image Share", true, true),
            ("RICOH IMAGING COMPANY, LTD.", "PENTAX K-3 Mark III Monochrome", null, "Image Sync", true, true),
            ("Ricoh", "RICOH GR IV HDF", null, "GR WORLD", true, true),
            ("Leica", "LEICA Q3 Monochrom", null, "Leica FOTOS", true, true),
            ("Hasselblad", "X1D II 50C", null, "Phocus Mobile", true, false),
            ("Hasselblad", "X2D II 100C", null, "Phocus Mobile", true, true)
        ];
        foreach (var (maker, model, firmware, appName, apple, google) in matches)
        {
            var app = CompanionApps.For(new(model, maker, null, firmware, null, null));
            Check(app?.Name == appName && (app.AppStore is not null) == apple && (app.GooglePlay is not null) == google,
                $"{model} has the confirmed companion app and platform links");
        }

        Check(CompanionApps.For(new("ILCE-7M4", "Sony", null, null, null, null)) is
            { AppStore: null, GooglePlay: null, Compatibility: not null }, "Missing Sony firmware shows a compatibility link");
        Check(CompanionApps.For(new("ILCE-7M4", "Sony", null, "1.10", null, null, ReadingSource.Arw)) is
            { AppStore: null, GooglePlay: null, Compatibility: not null }, "Photo firmware may be out of date after a Sony app migration");
        Check(CompanionApps.For(new("X-T5", "Fujifilm", null, "2.00", null, null)) is
            { AppStore: not null, HasMechanicalCount: false } && CompanionApps.For(new("X-T5", "Fujifilm", null, "3.01", null, null)) is
            { HasMechanicalCount: true }, "X-T5 gains the XApp mechanical counter with firmware 3.01");
        Check(CompanionApps.For(new("X-E5", "Unknown maker", null, null, null, null)) is null &&
            CompanionApps.For(new("X-E50", "Fujifilm", null, null, null, null)) is null &&
            CompanionApps.For(new("Canon EOS 5D Mark III", "Canon", null, null, null, null)) is null,
            "Unconfirmed model and maker combinations do not inherit mobile links");

        (string Maker, string Model, string App)[] desktopMatches =
        [
            ("Canon", "Canon EOS 5D Mark III", "EOS Utility"),
            ("Nikon Corporation", "Z5_2C", "NX Tether"),
            ("Sony", "α7 IV", "Imaging Edge Desktop"),
            ("Fujifilm", "Fujifilm X-E5", "FUJIFILM X RAW STUDIO"),
            ("Panasonic", "LUMIX S5 II", "LUMIX Tether"),
            ("OM Digital Solutions Corporation", "Olympus OM-D E-M1MarkIII", "OM Capture"),
            ("RICOH IMAGING COMPANY, LTD.", "PENTAX K-3 Mark III Monochrome", "Digital Camera Utility 5"),
            ("Ricoh", "RICOH GR II", "Digital Camera Utility 5"),
            ("Leica", "LEICA Q3 Monochrom", "Adobe Lightroom Classic"),
            ("Hasselblad", "907X & CFV 100C", "Phocus")
        ];
        foreach (var (maker, model, appName) in desktopMatches)
            Check(CompanionApps.DesktopFor(new(model, maker, null, null, null, null)).Any(app =>
                app.Name == appName && app.Windows is not null && app.MacOS is not null),
                $"{model} has {appName} for Windows and macOS");

        Check(CompanionApps.DesktopFor(new("X-HF1", "Fujifilm", null, null, null, null)).Count == 0 &&
            CompanionApps.DesktopFor(new("X-E50", "Fujifilm", null, null, null, null)).Count == 0 &&
            CompanionApps.DesktopFor(new("X-E5", "Unknown maker", null, null, null, null)).Count == 0,
            "Desktop apps require a verified model and maker");

        var webcam = CompanionApps.DesktopFor(new("D750", "Nikon", null, null, null, null))
            .Single(app => app.Name == "Nikon Webcam Utility");
        Check(webcam.ForPlatform(isMacOS: false) == (webcam.Windows, "Windows") &&
            webcam.ForPlatform(isMacOS: true) == (webcam.MacOS, "macOS") && webcam.Windows != webcam.MacOS,
            "A desktop app with separate installers selects the current operating system");

        var shutterCheck = CompanionApps.DesktopFor(new("Canon EOS 5D Mark III", "Canon", null, null, null, null))
            .Single(app => app.Name == "ShutterCheck");
        var transmitter = CompanionApps.DesktopFor(new("PENTAX K-3 Mark III Monochrome", "RICOH", null, null, null, null))
            .Single(app => app.Name == "IMAGE Transmitter 2");
        Check(shutterCheck.ForPlatform(isMacOS: false) is (null, "macOS") &&
            shutterCheck.ForPlatform(isMacOS: true) is (not null, "macOS") &&
            transmitter.ForPlatform(isMacOS: false) is (not null, "Windows") &&
            transmitter.ForPlatform(isMacOS: true) is (null, "Windows"),
            "Single-platform desktop apps show an installer on their platform and a notice on the other");
    }

    private static void CheckAdditionalUsbCameras()
    {
        (string Id, string? Name, string Model, string Maker)[] bodies =
        [
            ("usb#vid_04cb&pid_0313", null, "X-E5", "Fujifilm"),
            ("wpd#fuji", "FUJIFILM X-T30 III", "X-T30 III", "Fujifilm"),
            ("wpd#sony", "ILCE-7RM6", "ILCE-7RM6", "Sony"),
            ("wpd#panasonic", "DC-S1M2", "DC-S1M2", "Panasonic"),
            ("wpd#om", "OM-5 Mark II", "OM-5 Mark II", "OM System"),
            ("wpd#olympus", "E-M1MarkIII", "E-M1MarkIII", "Olympus"),
            ("wpd#pentax", "PENTAX K-3 Mark III Monochrome", "PENTAX K-3 Mark III Monochrome", "Pentax"),
            ("wpd#ricoh", "RICOH GR IV HDF", "RICOH GR IV HDF", "Ricoh"),
            ("wpd#leica", "LEICA Q3 Monochrom", "LEICA Q3 Monochrom", "Leica"),
            ("wpd#hasselblad", "X2D II 100C", "X2D II 100C", "Hasselblad"),
            ("wpd#future", "Digital Camera", "Future camera", "New brand")
        ];
        var devices = bodies.Select(body => new CameraDevice(body.Id, body.Name)).ToArray();
        var manager = new EmulatedDeviceManager([.. devices,
            new("usb#vid_054c&pid_ffff", "Sony Xperia"),
            new("usb#vid_1234&pid_5678", "Canon EOS R7")]);
        manager.DeviceTypes["usb#vid_054c&pid_ffff"] = 3;
        manager.DeviceTypes["wpd#future"] = 1;
        Check(WindowsCamera.Find(manager).SequenceEqual(devices),
            "USB discovery recognizes camera families, known product IDs and Windows camera devices while excluding phones and unrelated devices");

        foreach (var (id, name, model, maker) in bodies)
        {
            CameraExchange[] requests = id.Contains("vid_04cb", StringComparison.Ordinal)
                ? [Read(0x1015, [], 0xD21B) with { Response = 0x200A, ResponseBeforeData = true }] : [];
            var reading = new EmulatedCamera(new(id, name), requests)
            {
                Model = model, Maker = maker, Storage =
                    [new(4, 0, 200, 0), new(4, 0, null, null, DeviceIoError), new(4, 1, 200, 8), new(1, 1, 200, 0)]
            }.Read();
            Check(reading.Model == model && reading.Maker == maker && reading.Count is null && reading.Battery == 72 &&
                reading.StorageIssues == (CameraStorageIssues.Full | CameraStorageIssues.ReadOnly),
                $"{model} reports USB metadata and multiple storage issues without sending Canon commands");
        }

        Check(new EmulatedCamera(new("wpd#future", "Digital Camera")) { Model = "EOS in a camera name" }.Read().Count is null,
            "An EOS-like model name from a generic device cannot enable Canon commands");
        Console.WriteLine("Additional USB camera discovery and metadata checks passed.");
    }

    private static void CheckCameraStatusDashboard(MainWindow window, string? renderFolder)
    {
        byte[] ids = [3, 0, 0, 0, 0, 0, 1, 0, 1, 0, 1, 0, 1, 0, 2, 0];
        byte[] full = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(full, 4);
        BinaryPrimitives.WriteUInt64LittleEndian(full.AsSpan(6), 1024);

        var (lumix, nikon, fuji, sony, leica) = Task.Run(() =>
        {
            var lumix = new EmulatedCamera(new("wpd#panasonic", "DC-GH7"), Read(0x1004, ids),
                Read(0x1005, full, 0x00010001),
                Read(0x1005, [], 0x00020001) with { Response = 0x2013, ResponseBeforeData = true })
            { Model = "DC-GH7", Maker = "Panasonic", Storage = [] }.Read();
            var nikon = new EmulatedCamera(new("usb#vid_04b0&pid_0442", "Nikon Z 7"),
                Read(0x1015, [1], 0xD1B2), Read(0x1004, []) with { Response = 0xA022 })
            { Maker = "Nikon", Storage = [] }.Read();
            var fuji = new EmulatedCamera(new("usb#vid_04cb&pid_0313", "FUJIFILM X-E5"),
                Read(0x1015, [2, 0], 0xD21B)) { Maker = "Fujifilm" }.Read();
            var sony = new EmulatedCamera(new("usb#vid_054c&pid_0f50", "ILCE-7M5"),
                Read(0x9204, [2], 0xD251)) { Maker = "Sony" }.Read();
            var leica = new EmulatedCamera(new("wpd#leica", "Leica M11"),
                Read(0x1004, Counter(0))) { Maker = "Leica", Storage = [new(4, uint.MaxValue, 0, 0)] }.Read();
            return (lumix, nikon, fuji, sony, leica);
        }).GetAwaiter().GetResult();

        window.ShowUsbReading(lumix, null);
        Check(Label(window, "CameraWarningsText") ==
            "A camera storage volume is full.\nUse another card or back up your files before deleting any.\n\n" +
            "The camera reported that storage is unavailable.\nIf you use a memory card, turn the camera off and reinsert it or try a compatible spare. Reconnect and rescan.",
            "The shared PTP route explains both storage problems and shows an action for each");
        if (renderFolder is not null)
            Render((Grid)window.FindName("Dashboard"), renderFolder, "usb-storage-issues", 786, 880);

        window.ShowUsbReading(nikon, null);
        Check(Label(window, "CameraErrorText") == "The camera’s error indicator is active. Check its display for details." &&
            Label(window, "CameraErrorActionText") == "Follow any instructions shown there. If the error returns, save the code or message for support." &&
            Label(window, "CameraWarningsText") ==
                "The camera reported unformatted storage.\nBack up any files before formatting the card in the camera. You can also try another compatible card.",
            "A Nikon display warning and a Nikon storage response both reach the dashboard");

        window.ShowUsbReading(fuji, null);
        Check(Label(window, "CameraErrorText") == "Unknown camera error (0x00000002). Check the camera display for details.",
            "Fujifilm error codes with no verified mapping keep their value");

        window.ShowUsbReading(sony, null);
        Check(Label(window, "CameraErrorText") == "The camera reported an overheating warning." &&
            Label(window, "CameraErrorActionText") == "Turn the camera off and let it cool before turning it back on." &&
            Label(window, "CameraWarningsText") == "", "Sony thermal status is decoded and old storage issues are cleared");

        window.ShowUsbReading(leica, null);
        Check(Label(window, "CameraErrorText") == "Internal camera error status unavailable." &&
            Label(window, "CameraErrorActionText") == "" && Label(window, "CameraWarningsText") == "",
            "Placeholder storage data and an empty PTP storage list do not imply a camera fault");
    }

    private static void CheckAdditionalPhotos(string preferencePath, string folder, string? renderFolder)
    {
        const int noteOffset = 0x180;
        const int rafTiffOffset = 0x80;
        byte[] fuji = PhotoMetadata("FUJIFILM", "X-E5", "Digital Camera X-E5 Ver1.10");
        "FUJIFILM\x0c\0\0\0"u8.CopyTo(fuji.AsSpan(noteOffset));
        WriteShort(fuji, noteOffset + 12, 1);
        WriteTag(fuji, noteOffset + 14, 0x1438, 3, 1, 0x80C7);
        byte[] preview = JpegWithExif(fuji);
        var raf = new byte[rafTiffOffset - 12 + preview.Length];
        "FUJIFILMCCD-RAW "u8.CopyTo(raf);
        BinaryPrimitives.WriteUInt32BigEndian(raf.AsSpan(84), rafTiffOffset - 12);
        BinaryPrimitives.WriteUInt32BigEndian(raf.AsSpan(88), (uint)preview.Length);
        preview.CopyTo(raf, rafTiffOffset - 12);

        byte[] sony = PhotoMetadata("SONY", "ILCE-7M5", "ILCE-7M5 v1.00");
        WriteShort(sony, noteOffset, 1);
        WriteTag(sony, noteOffset + 2, 0x9050, 7, 16, 0x200);
        sony[0x20A] = 155 * 155 * 155 % 249;
        sony[0x20B] = 8; // Encoded 2, giving a count of 667.

        byte[] pentax = PhotoMetadata("RICOH IMAGING COMPANY, LTD.", "PENTAX K-3 Mark III Monochrome", "PENTAX K-3 Mark III Monochrome Ver. 2.10");
        "PENTAX \0II"u8.CopyTo(pentax.AsSpan(noteOffset));
        WriteShort(pentax, noteOffset + 10, 3);
        WriteTag(pentax, noteOffset + 12, 0x0006, 7, 4, 0x040CE707);
        WriteTag(pentax, noteOffset + 24, 0x0007, 7, 3, 0x00101509);
        WriteTag(pentax, noteOffset + 36, 0x005D, 7, 4, 0x14E20DF1);

        var window = new MainWindow(preferencePath);
        RunScanCheck(window, async () =>
        {
            var companionPanel = (StackPanel)window.FindName("CompanionPanel");
            var desktopRows = (StackPanel)window.FindName("DesktopCompanionRows");
            var appStore = (Hyperlink)window.FindName("AppStoreLink");
            var googlePlay = (Hyperlink)window.FindName("GooglePlayLink");
            var compatibility = (Hyperlink)window.FindName("CompatibilityLink");
            Check(companionPanel.Visibility == Visibility.Collapsed, "No reading hides the companion section");
            window.ShowUsbReading(new("X-E5", "FUJIFILM", "TEST", "1.10", null, null), null);
            Check(((StackPanel)window.FindName("ManualPanel")).Visibility == Visibility.Visible &&
                Label(window, "HelpText").Contains("Number of shutter movements"), "X-E5 USB points to the XApp counter");
            Check(companionPanel.Visibility == Visibility.Visible && Label(window, "CompanionAppName") == "FUJIFILM XApp" &&
                appStore.NavigateUri.AbsoluteUri == "https://apps.apple.com/app/id1586089681" &&
                googlePlay.NavigateUri.AbsoluteUri == "https://play.google.com/store/apps/details?id=com.fujifilm.xapp",
                "The X-E5 USB dashboard shows both official XApp store links");

            await window.ReadPhotoAsync(WritePhoto("x-e5.RAF", raf));
            Check(Label(window, "ModelTitle") == "Fujifilm X-E5" && Label(window, "SourceValue") == "RAF" &&
                Label(window, "FirmwareValue") == "1.10" && Label(window, "CountText") == "199" &&
                Label(window, "ReadingLabel") == "Image count" && Label(window, "ZoneText") == "Usage unavailable",
                "The X-E5 RAF image count is decoded and displayed without a mechanical usage zone");
            Check(Label(window, "CompanionAppName") == "FUJIFILM XApp" && googlePlay.NavigateUri.Query == "?id=com.fujifilm.xapp",
                "Importing an X-E5 RAF retains the XApp links");
            Check(desktopRows.Children.Count == 2 &&
                DesktopLinks().Any(link => link.NavigateUri.AbsolutePath.EndsWith("/x-raw-studio/")),
                "Importing an X-E5 RAF shows desktop tools alongside the mobile app");
            var manualCount = (TextBox)window.FindName("ManualCount");
            manualCount.Text = "129";
            ((Button)window.FindName("ApplyCountButton")).RaiseEvent(new(Button.ClickEvent));
            Check(Label(window, "CountText") == "129" && Label(window, "ReadingLabel") == "Mechanical shutter count" &&
                Label(window, "SourceValue") == "Manual count" && Label(window, "ZoneText") == "Usage unavailable",
                "The exact XApp count can be entered even without a durability rating");
            if (renderFolder is not null)
                Render((Grid)window.FindName("Dashboard"), renderFolder, "fujifilm-x-e5-mechanical", height: 780);
            ((Button)window.FindName("ClearCountButton")).RaiseEvent(new(Button.ClickEvent));
            Check(Label(window, "CountText") == "199" && Label(window, "SourceValue") == "RAF",
                "Reset restores the original photo count");

            await window.ReadPhotoAsync(WritePhoto("truncated.raf", raf[..(rafTiffOffset + 8)]));
            Check(Label(window, "ConnectionText") == "Photo could not be opened" && Label(window, "CountText") == "199",
                "A truncated RAF preserves the previous reading");

            await window.ReadPhotoAsync(WritePhoto("alpha-7v.ARW", sony));
            Check(Label(window, "SourceValue") == "ARW" && Label(window, "CountText") == "667" &&
                Label(window, "ReadingLabel") == "Mechanical shutter count", "Alpha 7 V mechanical ARW count");
            Check(Label(window, "CompanionAppName") == "Creators’ App" && googlePlay.NavigateUri.Query == "?id=jp.co.sony.ips.portalapp",
                "Switching readings replaces the previous companion app and links");

            byte[] sonyJpeg = (byte[])sony.Clone();
            sony.AsSpan(noteOffset, 14).CopyTo(sonyJpeg.AsSpan(noteOffset + 12));
            "SONY DSC \0\0\0"u8.CopyTo(sonyJpeg.AsSpan(noteOffset));
            await window.ReadPhotoAsync(WritePhoto("alpha-7v.jpg", JpegWithExif(sonyJpeg)));
            Check(Label(window, "SourceValue") == "JPEG" && Label(window, "CountText") == "667" &&
                Label(window, "ReadingLabel") == "Mechanical shutter count", "Sony JPEG MakerNote header and count");

            var nikonJpeg = JpegWithExif(NikonPhoto("Z5_2C", 400, 300, bigEndian: true));
            await window.ReadPhotoAsync(WritePhoto("z5iic.jpeg", nikonJpeg));
            Check(Label(window, "ModelTitle") == "Nikon Z 5IIC" && Label(window, "SourceValue") == "JPEG" &&
                Label(window, "CountText") == "300" && Label(window, "ReadingLabel") == "Mechanical shutter count",
                "Nikon JPEG MakerNote uses offsets relative to the Exif block and its own byte order");

            await window.ReadPhotoAsync(WritePhoto("fujifilm.jpg", preview));
            Check(Label(window, "ConnectionText") == "Photo could not be opened" && Label(window, "CountText") == "300",
                "A JPEG from an unsupported maker preserves the previous reading");

            sony[0x200] = 1;
            await window.ReadPhotoAsync(WritePhoto("alpha-7v-electronic.ARW", sony));
            Check(Label(window, "CountText") == "—", "An alternate Alpha 7 V block is not decoded as a mechanical count");

            WriteShort(pentax, 0x100, 0);
            WriteTag(pentax, 58, 0xC634, 1, 0x100, noteOffset);
            await window.ReadPhotoAsync(WritePhoto("k-3-monochrome.DNG", pentax));
            Check(Label(window, "SourceValue") == "DNG" && Label(window, "CountText") == "495" &&
                Label(window, "ReadingLabel") == "Camera-reported shutter count", "Pentax DNG private data yields the reported count");

            window.ShowUsbReading(new("Nikon D750", "Nikon", null, null, null, null), null);
            Check(appStore.NavigateUri is null && googlePlay.NavigateUri is not null,
                "An Android-only model clears the previous App Store link");
            Check(DesktopLinks().Select(link => link.NavigateUri.AbsolutePath).SequenceEqual(
                ["/en/download/sw/279.html", "/en/download/sw/220.html"]) &&
                DesktopLinks().All(link => link.Inlines.OfType<Run>().Single().Text == "Windows"),
                "The Windows dashboard shows only the matching D750 desktop links");
            window.ShowUsbReading(new("ILCE-7M4", "Sony", null, null, null, null), null);
            Check(appStore.NavigateUri is null && googlePlay.NavigateUri is null && compatibility.NavigateUri.Host == "www.sony.com" &&
                DesktopLinks().All(link => link.NavigateUri.Host == "support.d-imaging.sony.co.jp") && desktopRows.Children.Count == 2,
                "An unresolved mobile match shows its compatibility link alongside independent desktop tools");
            window.ShowUsbReading(new("Canon EOS 5D Mark III", "Canon", null, null, null, null), null);
            Check(companionPanel.Visibility == Visibility.Visible && appStore.NavigateUri is null && googlePlay.NavigateUri is null &&
                compatibility.NavigateUri is null && DesktopLinks().Any(link => link.NavigateUri.AbsolutePath.EndsWith("/eu.html")),
                "A camera with desktop apps only still shows the companion section");
            var shutterCheckRow = desktopRows.Children.Cast<WrapPanel>()
                .Single(row => ((TextBlock)row.Children[0]).Text == "ShutterCheck");
            var notice = (TextBlock)shutterCheckRow.Children[1];
            Check(notice.Text == "macOS only" && !notice.Inlines.OfType<Hyperlink>().Any(),
                "On Windows a macOS-only app shows a notice without an installer link");
            if (renderFolder is not null)
                Render((Grid)window.FindName("Dashboard"), renderFolder, "desktop-companion-apps", width: 820, height: 860);
            window.ShowUsbReading(new("X-E50", "Fujifilm", null, null, null, null), null);
            Check(companionPanel.Visibility == Visibility.Collapsed && desktopRows.Children.Count == 0 && compatibility.NavigateUri is null &&
                !Label(window, "HelpText").Contains("XApp"), "An unlisted camera clears companion links and XApp guidance");

            IEnumerable<Hyperlink> DesktopLinks() => desktopRows.Children.Cast<WrapPanel>()
                .SelectMany(row => row.Children.Cast<TextBlock>())
                .SelectMany(label => label.Inlines.OfType<Hyperlink>());
        });
        Console.WriteLine("Additional original-photo dashboard checks passed.");

        string WritePhoto(string name, byte[] data)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, data);
            return path;
        }

        static byte[] JpegWithExif(byte[] tiff)
        {
            var jpeg = new byte[12 + tiff.Length];
            Convert.FromHexString("FFD8FFE1").CopyTo(jpeg, 0);
            BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(4), (ushort)(8 + tiff.Length));
            "Exif\0\0"u8.CopyTo(jpeg.AsSpan(6));
            tiff.CopyTo(jpeg, 12);
            return jpeg;
        }

        static byte[] PhotoMetadata(string maker, string model, string software)
        {
            var data = new byte[0x280];
            "II\x2a\0\x08\0\0\0"u8.CopyTo(data);
            WriteShort(data, 8, 5);
            WriteTag(data, 10, 0x010F, 2, WriteText(0x48, maker), 0x48);
            WriteTag(data, 22, 0x0110, 2, WriteText(0x68, model), 0x68);
            WriteTag(data, 34, 0x0131, 2, WriteText(0xA0, software), 0xA0);
            WriteTag(data, 46, 0x8769, 4, 1, 0x100);
            WriteShort(data, 0x100, 1);
            WriteTag(data, 0x102, 0x927C, 7, 0x100, noteOffset);
            return data;

            uint WriteText(int offset, string text) => (uint)Encoding.ASCII.GetBytes(text + "\0", data.AsSpan(offset));
        }

        static void WriteShort(byte[] data, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);

        static void WriteTag(byte[] data, int offset, ushort tag, ushort type, uint count, uint value)
        {
            WriteShort(data, offset, tag);
            WriteShort(data, offset + 2, type);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4), count);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 8), value);
        }
    }
}
