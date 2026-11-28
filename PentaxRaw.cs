using System.Buffers.Binary;
using System.IO;

namespace CameraInspector;

internal static class PentaxRaw
{
    internal static CameraReading Read(string path, ReadingSource source)
    {
        using var file = File.OpenRead(path);
        var tiff = new TiffMetadata(file, 0, file.Length);
        var image = tiff.ReadDirectory(tiff.FirstDirectory);
        var model = tiff.Text(image, 0x0110);
        var maker = tiff.Text(image, 0x010F);
        if (model is null || (maker?.StartsWith("PENTAX", StringComparison.OrdinalIgnoreCase) != true &&
            !(maker?.StartsWith("RICOH", StringComparison.OrdinalIgnoreCase) == true &&
              model.StartsWith("PENTAX ", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Expected an original Pentax PEF or DNG.");

        var exif = tiff.Number(image, 0x8769) is uint exifOffset ? tiff.ReadDirectory(exifOffset) : [];
        string? serial = tiff.Text(exif, 0xA431);
        ShutterCount? count = null;
        var note = tiff.Block(exif, 0x927C, type: 7, minimumSize: 12);
        if (source == ReadingSource.Dng && note is null)
            note = tiff.Block(image, 0xC634, type: 1, minimumSize: 12);
        if (note is { } block)
        {
            Span<byte> header = stackalloc byte[10];
            tiff.ReadAt(block.Offset, header);
            if (header[..8].SequenceEqual("PENTAX \0"u8) &&
                (header[8..].SequenceEqual("II"u8) || header[8..].SequenceEqual("MM"u8)))
            {
                var pentax = new TiffMetadata(file, block.Offset, (long)block.Offset + block.Size,
                    littleEndian: header[8] == (byte)'I');
                var tags = pentax.ReadDirectory(10);
                serial = pentax.Text(tags, 0x0229) ?? serial;
                if (InlineValue(pentax, tags, 0x0006, 4) is uint date &&
                    InlineValue(pentax, tags, 0x0007, 3) is uint time &&
                    InlineValue(pentax, tags, 0x005D, 4) is uint encoded)
                    count = new(encoded ^ date ^ ~time, CountKind.Unspecified);
            }
        }

        var software = tiff.Text(image, 0x0131);
        string prefix = model + " Ver.";
        string? firmware = software?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? software[prefix.Length..].Trim() : null;
        string name = model.StartsWith("PENTAX ", StringComparison.OrdinalIgnoreCase) ? model[7..] : model;
        return new($"Pentax {name}", "Pentax", serial, firmware, null, count, source);
    }

    private static uint? InlineValue(TiffMetadata metadata, byte[] tags, ushort id, uint length)
    {
        var tag = metadata.Find(tags, id);
        if (tag.IsEmpty || metadata.UInt16(tag[2..]) != 7 || metadata.UInt32(tag[4..]) != length)
            return null;

        // Pentax stores these bytes in big-endian order even when the directory is little-endian.
        uint value = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
        return length == 3 ? value & 0xFFFFFF00 : value;
    }
}
