using System.Buffers.Binary;
using System.IO;

namespace CameraInspector;

internal static class FujifilmRaf
{
    internal static CameraReading Read(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[92];
        file.ReadExactly(header);
        if (!header[..16].SequenceEqual("FUJIFILMCCD-RAW "u8))
            throw new InvalidDataException("Expected an original Fujifilm RAF.");

        // RAF places the Exif segment in its embedded JPEG preview.
        long previewOffset = BinaryPrimitives.ReadUInt32BigEndian(header[84..]);
        long previewSize = BinaryPrimitives.ReadUInt32BigEndian(header[88..]);
        if (previewOffset < header.Length || previewSize < 2 || previewSize > file.Length - previewOffset)
            throw new InvalidDataException("Invalid RAF preview bounds.");
        var tiff = TiffMetadata.FromJpeg(file, previewOffset, previewOffset + previewSize);
        var image = tiff.ReadDirectory(tiff.FirstDirectory);
        string? model = tiff.Text(image, 0x0110);
        if (!string.Equals(tiff.Text(image, 0x010F), "FUJIFILM", StringComparison.OrdinalIgnoreCase) || model is null)
            throw new InvalidDataException("Expected Fujifilm camera metadata.");

        string? serial = null;
        ShutterCount? count = null;
        if (tiff.Number(image, 0x8769) is uint exifOffset)
        {
            var exif = tiff.ReadDirectory(exifOffset);
            serial = tiff.Text(exif, 0xA431);
            if (tiff.Block(exif, 0x927C, type: 7, minimumSize: 14) is { } note)
            {
                Span<byte> signature = stackalloc byte[12];
                tiff.ReadAt(note.Offset, signature);
                long start = file.Position - signature.Length;
                if (signature[..8].SequenceEqual("FUJIFILM"u8))
                {
                    var fuji = new TiffMetadata(file, start, start + note.Size, littleEndian: true);
                    var tags = fuji.ReadDirectory(BinaryPrimitives.ReadUInt32LittleEndian(signature[8..]));
                    if (fuji.Short(tags, 0x1438) is ushort images)
                        count = new((uint)images & 0x7FFF, CountKind.Image);
                }
            }
        }

        var software = tiff.Text(image, 0x0131);
        string firmwarePrefix = $"Digital Camera {model} Ver";
        string? firmware = software?.StartsWith(firmwarePrefix, StringComparison.OrdinalIgnoreCase) == true
            ? software[firmwarePrefix.Length..].Trim() : null;
        return new($"Fujifilm {model}", "Fujifilm", serial, firmware, null, count, ReadingSource.Raf);
    }
}
