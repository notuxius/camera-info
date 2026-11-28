using System.IO;

namespace CameraInspector;

internal static class CameraJpeg
{
    internal static CameraReading Read(string path)
    {
        using var file = File.OpenRead(path);
        var tiff = TiffMetadata.FromJpeg(file, 0, file.Length);
        string? maker = tiff.Text(tiff.ReadDirectory(tiff.FirstDirectory), 0x010F);
        return maker?.ToUpperInvariant() switch
        {
            "NIKON" or "NIKON CORPORATION" => NikonNef.ReadMetadata(tiff, ReadingSource.Jpeg),
            "SONY" => SonyArw.ReadMetadata(tiff, ReadingSource.Jpeg),
            _ => throw new InvalidDataException("Expected an original Nikon or Sony JPEG.")
        };
    }
}
