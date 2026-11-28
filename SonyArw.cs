using System.IO;

namespace CameraInspector;

internal static class SonyArw
{
    private static readonly byte[] Decipher = BuildDecipherTable();

    internal static CameraReading Read(string path)
    {
        using var file = File.OpenRead(path);
        return ReadMetadata(new TiffMetadata(file, 0, file.Length), ReadingSource.Arw);
    }

    internal static CameraReading ReadMetadata(TiffMetadata tiff, ReadingSource source)
    {
        var image = tiff.ReadDirectory(tiff.FirstDirectory);
        string? model = tiff.Text(image, 0x0110);
        if (!string.Equals(tiff.Text(image, 0x010F), "SONY", StringComparison.OrdinalIgnoreCase) || model is null)
            throw new InvalidDataException("Expected an original Sony photo.");

        ShutterCount? count = null;
        string? serial = null;
        if (tiff.Number(image, 0x8769) is uint exifOffset)
        {
            var exif = tiff.ReadDirectory(exifOffset);
            serial = tiff.Text(exif, 0xA431);
            if (tiff.Block(exif, 0x927C, type: 7, minimumSize: 6) is { } note)
            {
                uint headerSize = 0;
                if (source == ReadingSource.Jpeg)
                {
                    Span<byte> header = stackalloc byte[12];
                    if (note.Size >= header.Length)
                    {
                        tiff.ReadAt(note.Offset, header);
                        if (header.SequenceEqual("SONY DSC \0\0\0"u8))
                            headerSize = (uint)header.Length;
                    }
                }
                var tags = tiff.ReadDirectory(checked(note.Offset + headerSize), note.Size - headerSize);
                count = ReadCount(tiff, tags, model.ToUpperInvariant());
            }
        }

        string? software = tiff.Text(image, 0x0131);
        string prefix = model + " v";
        string? firmware = software?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? software[prefix.Length..] : null;
        return new($"Sony {model}", "Sony", serial, firmware, null, count, source);
    }

    private static ShutterCount? ReadCount(TiffMetadata tiff, byte[] tags, string model)
    {
        // Sony tag 0x9050 has model-specific layouts. Some recent bodies omit the mechanical layout in electronic mode.
        var (offset, kind) = model switch
        {
            "ILCE-6700" or "ILCE-7CM2" or "ILCE-7CR" or "ILCE-1M2" or "ILCE-7M5" or "ILCE-7RM6" or "ILME-FX2"
                => (10u, CountKind.Mechanical),
            "ILCE-6100" or "ILCE-6100A" or "ILCE-6300" or "ILCE-6400" or "ILCE-6400A" or
            "ILCE-6500" or "ILCE-6600" or "ILCE-7C" or "ILCE-7M3" or "ILCE-7RM2" or "ILCE-7RM3" or
            "ILCE-7RM3A" or "ILCE-7RM4" or "ILCE-7RM4A" or "ILCE-7SM2" or "ILCE-9" or "ILCE-9M2" or
            "ILCA-99M2" or "ZV-E10" or "ILCE-1" or "ILCE-7M4" or "ILCE-7RM5" or "ILCE-7SM3" or "ILME-FX3"
                => (0x3Au, CountKind.Unspecified),
            _ => (0u, CountKind.Unspecified)
        };
        if (offset == 0 || tiff.Block(tags, 0x9050, type: 7, minimumSize: 5) is not { } block ||
            block.Size < offset + 4)
            return null;
        if (model is "ILCE-1M2" or "ILCE-7M5" or "ILCE-7RM6")
        {
            Span<byte> prefix = stackalloc byte[5];
            tiff.ReadAt(block.Offset, prefix);
            if (!prefix.SequenceEqual("\0\0\0\0\0"u8))
                return null;
        }

        Span<byte> value = stackalloc byte[3];
        tiff.ReadAt((long)block.Offset + offset, value);
        uint count = (uint)(Decipher[value[0]] | (Decipher[value[1]] << 8) | (Decipher[value[2]] << 16));
        return new(count, kind);
    }

    private static byte[] BuildDecipherTable()
    {
        var table = new byte[256];
        for (int value = 0; value < table.Length; value++)
            table[value] = (byte)value;
        for (int value = 2; value < 248; value++)
            table[value * value * value % 249] = (byte)value;
        return table;
    }
}
