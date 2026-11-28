using System.IO;
using System.Text.RegularExpressions;

namespace CameraInspector;

internal static partial class NikonNef
{
    private const string Prefix = "Nikon ";

    internal static string? ModelName(string? name)
    {
        if (NikonDslr.ModelName(name) is { } dslr)
            return dslr;
        string model = name?.Trim() ?? "";
        if (model.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            model = model[Prefix.Length..].TrimStart();
        var match = ZModel().Match(model);
        if (!match.Success)
            return null;
        string body = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value).ToUpperInvariant();
        string generation = match.Groups[2].Value.ToUpperInvariant().Replace("_2", "II").Replace("_3", "III");
        return body switch
        {
            "R" => "Nikon ZR",
            "F" or "FC" => "Nikon Z " + body.ToLowerInvariant(),
            _ => "Nikon Z " + body + generation
        };
    }

    [GeneratedRegex(@"^Z\s*(?:([1-9][0-9]*)(?:\s*(_2C?|_3|IIC|III|II))?|(F[C]?|R))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZModel();

    internal static CameraReading Read(string path)
    {
        if (!Path.GetExtension(path).Equals(".nef", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected a Nikon NEF file.");

        using var file = File.OpenRead(path);
        return ReadMetadata(new TiffMetadata(file, 0, file.Length), ReadingSource.Nef);
    }

    internal static CameraReading ReadMetadata(TiffMetadata tiff, ReadingSource source)
    {
        var image = tiff.ReadDirectory(tiff.FirstDirectory);
        string? maker = tiff.Text(image, 0x010F);
        string? model = ModelName(tiff.Text(image, 0x0110));
        if (model is null || !string.Equals(maker, "NIKON CORPORATION", StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(maker, "NIKON", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected an original Nikon photo.");

        string? serial = null;
        ShutterCount? count = null;
        if (tiff.Number(image, 0x8769) is uint exifOffset)
        {
            var exif = tiff.ReadDirectory(exifOffset);
            serial = tiff.Text(exif, 0xA431);
            if (tiff.Block(exif, 0x927C, type: 7, minimumSize: 18) is { } note)
            {
                Span<byte> signature = stackalloc byte[7];
                tiff.ReadAt(note.Offset, signature);
                if (signature.SequenceEqual("Nikon\0\x02"u8))
                {
                    // Nikon type 3 MakerNotes use a TIFF header and relative offsets of their own.
                    var nikon = tiff.ReadNestedTiff(note.Offset, note.Size, 10);
                    var tags = nikon.ReadDirectory(nikon.FirstDirectory);
                    serial = nikon.Text(tags, 0x001D) ?? serial;
                    if (model is not ("Nikon Z 8" or "Nikon Z 9" or "Nikon ZR") &&
                        Counter(nikon, tags, 0x0037) is uint mechanical)
                        count = new(mechanical, CountKind.Mechanical);
                    else if (Counter(nikon, tags, 0x00A7) is uint reported)
                        count = new(reported, CountKind.Unspecified);
                }
            }
        }

        string? firmware = tiff.Text(image, 0x0131);
        return new(model, "Nikon", serial, firmware?.StartsWith("Ver.", StringComparison.OrdinalIgnoreCase) == true
            ? firmware[4..].Trim() : null, null, count, source);
    }

    private static uint? Counter(TiffMetadata tiff, byte[] tags, ushort id) => tiff.Number(tags, id) switch
    {
        // Nikon uses 0xFFFFF7FF for an unavailable shutter count.
        0xFFFFF7FF or uint.MaxValue => null,
        var value => value
    };
}
