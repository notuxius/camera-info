using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CameraInspector;

internal static partial class Program
{
    private static void CheckNikonUsb()
    {
        var d850 = new CameraDevice(@"\\?\usb#vid_04b0&pid_0441#emulated", "Digital Camera");
        var d750 = new CameraDevice(@"\\?\swd#wpd#emulated", "NIKON DSC D750");
        var next = new CameraDevice(@"\\?\usb#vid_04b0&pid_0442#emulated", "Nikon Z 7");
        var devices = WindowsCamera.Find(new EmulatedDeviceManager(d850, d750, next));
        Check(devices.Select(device => device.Name).SequenceEqual(["Nikon D850", "Nikon D750", "Nikon Z 7"]),
            "Discovery includes Nikon DSLRs and Z bodies");

        var reading = new EmulatedCamera(d850, Read(0x1015, [1], 0xD1B2)) { Model = "D850", Maker = " Nikon Corporation " }.Read(devices[0]);
        Check(reading is { Model: "Nikon D850", Maker: "Nikon Corporation", Serial: "EMULATED-123", Battery: 72, Count: null,
            Source: ReadingSource.Usb, DeviceErrorCode: 1, ErrorSource: CameraErrorSource.NikonDisplay },
            "Nikon USB reads Windows metadata and the camera's error indicator");
        Check(new EmulatedCamera(d750, Read(0x1004, Counter(0))) { MissingDetails = true }.Read(devices[1]) is
            { Model: "Nikon D750", Maker: "Nikon", Count: null, StorageIssues: null },
            "Nikon USB retains the identified model when Windows metadata is unavailable");
        Check(new EmulatedCamera(next, Read(0x1015, [0, 0], 0xD1B2)) { Model = "Z 7", Maker = "Nikon" }.Read(devices[2]) is
            { Model: "Nikon Z 7", Count: null, DeviceErrorCode: null }, "An unfamiliar Nikon error payload stays unavailable");
        Check(CameraRatings.For("Nikon D850") is { Cycles: 200_000 } &&
              CameraRatings.For("Nikon D3500") is null && CameraRatings.For("Nikon Z 7") is { Cycles: 200_000 } &&
              CameraRatings.For("Nikon Z 50II") is { Cycles: 100_000 } && CameraRatings.For("Nikon Z 9") is null,
            "Nikon durability ratings are published and model-specific");
    }

    private static void CheckNikonPhotos(string preferencePath, string folder, string? renderFolder)
    {
        string mechanical = WritePhoto("mechanical", NikonPhoto("D850", 128_765, 100_001, bigEndian: true));
        string reported = WritePhoto("reported", NikonPhoto("D750", 128_765, null));
        string missing = WritePhoto("missing", NikonPhoto("D850", 0xFFFFF7FF, null));
        string unsupported = WritePhoto("unsupported", NikonPhoto("COOLPIX P1100", 128_765, 100_000));
        string truncated = WritePhoto("truncated", NikonPhoto("D850", 1, 0)[..0x185]);
        string zero = WritePhoto("zero", NikonPhoto("D6", 1, 0));
        Check(NikonNef.Read(zero).Count is { Value: 0, Kind: CountKind.Mechanical }, "A dedicated Nikon count of zero stays valid");
        Check(NikonNef.Read(WritePhoto("z5_2", NikonPhoto("Z5_2", 501, 404))) is
            { Model: "Nikon Z 5II", Count: { Value: 404, Kind: CountKind.Mechanical } }, "Nikon Z generation aliases and mechanical count");
        Check(NikonNef.ModelName("NIKON Z5_2C") == "Nikon Z 5IIC" && CameraRatings.For("Nikon Z 5IIC") is { Cycles: 200_000 },
            "The Z5IIC has its own model name and published durability rating");
        Check(NikonNef.Read(WritePhoto("z8", NikonPhoto("Z 8", 501, 0))) is
            { Model: "Nikon Z 8", Count: { Value: 501, Kind: CountKind.Unspecified } }, "Electronic-only Nikon Z 8 displays the reported count");

        var window = new MainWindow(preferencePath);
        RunScanCheck(window, async () =>
        {
            window.ShowLiveReading(new("Nikon D850", "Nikon", "1", "1.00", 72, null), null);
            Check(Label(window, "HelpText").StartsWith("Select “Scan for cameras” to refresh camera details over USB. " +
                "For the shutter count, select “Open original photo” and choose a recent original NEF / JPEG photo from this camera.",
                StringComparison.Ordinal), "Nikon USB distinguishes rescanning camera details from opening a photo for the count");

            await window.ReadPhotoAsync(mechanical);
            Check(Label(window, "ModelTitle") == "Nikon D850" && Label(window, "SerialValue") == "00012345" &&
                Label(window, "FirmwareValue") == "1.23" && Label(window, "BatteryValue") == "—" &&
                Label(window, "ConnectionText") == "Original photo loaded" && Label(window, "SourceValue") == "NEF",
                "The photo opens with metadata from the file");
            CheckCameraIcon(window, "ScaleLow", "A successful photo read uses green");
            Check(Label(window, "CountText") == "100,001" && Label(window, "ReadingLabel") == "Mechanical shutter count" &&
                Label(window, "ZoneText") == "Elevated usage" && ((TextBlock)window.FindName("ShutterHistoryText")).Visibility == Visibility.Collapsed,
                "The mechanical MakerNote takes precedence and leaves live USB history separate");
            Check(Label(window, "HelpText") == "Connect your camera by USB and select “Scan for cameras” to see what it reports. " +
                "To update a photo-based reading, select “Open original photo” and choose a newer original photo from the same camera.",
                "A photo-based reading explains when to rescan or choose a newer photo");
            if (renderFolder is not null)
                Render((Grid)window.FindName("Dashboard"), renderFolder, "nikon-nef");

            await window.ReadPhotoAsync(reported);
            Check(Label(window, "CountText") == "128,765" && Label(window, "ReadingLabel") == "Camera-reported shutter count" &&
                Label(window, "ZoneText") == "Usage unavailable", "The general counter is not used to calculate wear");

            foreach (string invalid in new[] { unsupported, truncated })
            {
                await window.ReadPhotoAsync(invalid);
                Check(Label(window, "ConnectionText") == "Photo could not be opened" && Label(window, "ModelTitle") == "Nikon D750",
                    "An invalid photo shows an error and preserves the previous reading");
                CheckCameraIcon(window, "ScaleHigh", "A failed photo read uses red even while previous metadata remains visible");
            }

            await window.ReadPhotoAsync(missing);
            Check(Label(window, "CountText") == "—" && Label(window, "UsageNote") == "This photo has no readable counter.",
                "The Nikon unavailable marker never displays as a count");
        });
        Console.WriteLine("Nikon NEF parsing and dashboard checks passed.");

        string WritePhoto(string name, byte[] bytes)
        {
            string path = Path.Combine(folder, name + ".NEF");
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }

    private static byte[] NikonPhoto(string model, uint? reported, uint? mechanical, bool bigEndian = false)
    {
        const int note = 0x180, nikonHeader = note + 10;
        var bytes = new byte[0x280];
        WriteHeader(0, bigEndian);
        WriteShort(8, 4, bigEndian);
        WriteTag(10, 0x010F, 2, WriteText(0x80, "NIKON CORPORATION"), 0x80, bigEndian);
        WriteTag(22, 0x0110, 2, WriteText(0xA0, "NIKON " + model), 0xA0, bigEndian);
        WriteTag(34, 0x0131, 2, WriteText(0xC0, "Ver.1.23"), 0xC0, bigEndian);
        WriteTag(46, 0x8769, 4, 1, 0x100, bigEndian);
        WriteShort(0x100, 1, bigEndian);
        WriteTag(0x102, 0x927C, 7, 0x100, note, bigEndian);

        "Nikon\0\x02\x10\0\0"u8.CopyTo(bytes.AsSpan(note));
        bool noteEndian = !bigEndian;
        WriteHeader(nikonHeader, noteEndian);
        int tags = nikonHeader + 10;
        WriteShort(nikonHeader + 8, (ushort)(1 + (mechanical.HasValue ? 1 : 0) + (reported.HasValue ? 1 : 0)), noteEndian);
        WriteTag(tags, 0x001D, 2, WriteText(nikonHeader + 0x80, "00012345"), 0x80, noteEndian);
        tags += 12;
        if (mechanical is uint mechanicalValue)
        {
            WriteTag(tags, 0x0037, 4, 1, mechanicalValue, noteEndian);
            tags += 12;
        }
        if (reported is uint reportedValue)
            WriteTag(tags, 0x00A7, 4, 1, reportedValue, noteEndian);
        return bytes;

        uint WriteText(int offset, string text) => (uint)Encoding.ASCII.GetBytes(text + "\0", bytes.AsSpan(offset));

        void WriteHeader(int offset, bool big)
        {
            (big ? "MM"u8 : "II"u8).CopyTo(bytes.AsSpan(offset));
            WriteShort(offset + 2, 42, big);
            WriteLong(offset + 4, 8, big);
        }

        void WriteTag(int offset, ushort id, ushort type, uint count, uint value, bool big)
        {
            WriteShort(offset, id, big);
            WriteShort(offset + 2, type, big);
            WriteLong(offset + 4, count, big);
            WriteLong(offset + 8, value, big);
        }

        void WriteShort(int offset, ushort value, bool big)
        {
            if (big)
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), value);
            else
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        }

        void WriteLong(int offset, uint value, bool big)
        {
            if (big)
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        }
    }
}
