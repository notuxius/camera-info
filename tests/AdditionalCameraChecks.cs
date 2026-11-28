using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows.Controls;
using CameraInspector;

internal static partial class Program
{
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
            var reading = new EmulatedCamera(new(id, name)) { Model = model, Maker = maker }.Read();
            Check(reading.Model == model && reading.Maker == maker && reading.Count is null && reading.Battery == 72,
                $"{model} reads USB metadata without sending Canon commands");
        }

        Check(new EmulatedCamera(new("wpd#future", "Digital Camera")) { Model = "EOS in a camera name" }.Read().Count is null,
            "An EOS-like model name from a generic device cannot enable Canon commands");
        Console.WriteLine("Additional USB camera discovery and metadata checks passed.");
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
            window.ShowUsbReading(new("X-E5", "FUJIFILM", "TEST", "1.10", null, null), null);
            Check(((StackPanel)window.FindName("ManualPanel")).Visibility == System.Windows.Visibility.Visible &&
                Label(window, "HelpText").Contains("Number of shutter movements"), "X-E5 USB points to the XApp counter");

            await window.ReadPhotoAsync(WritePhoto("x-e5.RAF", raf));
            Check(Label(window, "ModelTitle") == "Fujifilm X-E5" && Label(window, "SourceValue") == "RAF" &&
                Label(window, "FirmwareValue") == "1.10" && Label(window, "CountText") == "199" &&
                Label(window, "ReadingLabel") == "Image count" && Label(window, "ZoneText") == "Usage unavailable",
                "The X-E5 RAF image count is decoded and displayed without a mechanical usage zone");
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
