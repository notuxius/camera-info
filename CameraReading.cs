namespace CameraInspector;

internal sealed record CameraDevice(string Id, string? Name)
{
    internal const string UnknownName = "Unknown camera";
}

internal enum CountKind { Mechanical, Unspecified, Image }
internal enum ReadingSource { Usb, Nef, Raf, Arw, Pef, Dng, Jpeg }

internal readonly record struct ShutterCount(uint Value, CountKind Kind, uint? UsbProperty = null, bool IsUpperBound = false)
{
    internal bool HasRange => IsUpperBound && Value > 0;
    internal uint LowerBound => HasRange ? Value - Math.Min(Value - 1, CanonShutter.ReleaseStep - 1) : Value;
}

internal sealed record CameraReading(
    string Model, string Maker, string? Serial, string? Firmware, uint? Battery, ShutterCount? Count,
    ReadingSource Source = ReadingSource.Usb);

internal sealed record CameraRating(uint Cycles, string Source);

internal static class CameraRatings
{
    private const string R5Comparison = "https://downloads.canon.com/nw/camera/products/eos/product-1/pdfs/EOS-R5-improvements-part-1.pdf";
    private const string ProDslrBrochure = "https://downloads.canon.com/cpr/software/camera/CPP_5d_7d.pdf";
    private const string R6MarkIII = "https://www.usa.canon.com/shop/p/eos-r6-mark-iii-with-cropping-guide-firmware";
    internal const string NikonRatingSource = "https://www.nikonimgsupport.com/eu/BV_article?articleNo=000044853&configured=1&lang=en_GB&setRedirect=true";

    internal static CameraRating? For(string model) => CanonShutter.ModelKey(model) switch
    {
        "EOSR" => new(200_000, R5Comparison),
        "EOSR5" => new(500_000, R5Comparison),
        "EOSR5MARKII" => new(500_000, R6MarkIII),
        "EOSR6" => new(300_000, R5Comparison),
        "EOSR6MARKII" => new(400_000, "https://sg.canon/en/consumer/eos-r6-mark-ii/body/specification"),
        "EOSR6MARKIII" => new(500_000, R6MarkIII),
        "EOSR7" => new(200_000, "https://sg.canon/en/consumer/eos-r7/body/specification"),
        "EOS5DMARKII" => new(150_000, ProDslrBrochure),
        "EOS5DMARKIII" => new(150_000, "https://www.cla.canon.com/en/p/eos-5d-mark-iii"),
        "EOS6D" => new(100_000, "https://www.cla.canon.com/en/p/eos-6d"),
        "EOS7D" => new(150_000, ProDslrBrochure),
        "EOS7DMARKII" => new(200_000, "https://www.cla.canon.com/en/p/eos-7d-mark-ii"),
        "EOS80D" => new(100_000, "https://www.cla.canon.com/en/p/eos-80d"),
        _ => NikonDslr.RatingFor(model) ?? NikonZRatingFor(model)
    };

    private static CameraRating? NikonZRatingFor(string model) => NikonNef.ModelName(model) switch
    {
        "Nikon Z 5" or "Nikon Z 5II" or "Nikon Z 5IIC" or "Nikon Z 6" or "Nikon Z 6II" or "Nikon Z 6III"
            or "Nikon Z 7" or "Nikon Z 7II" or "Nikon Z f" => new(200_000, NikonRatingSource),
        "Nikon Z 30" or "Nikon Z 50" or "Nikon Z 50II" or "Nikon Z fc" => new(100_000, NikonRatingSource),
        _ => null
    };
}

internal enum UsageZone { Low, Elevated, High }

internal readonly record struct ShutterUsage(double Ratio, UsageZone Zone)
{
    internal static ShutterUsage? From(ShutterCount? count, CameraRating? rating)
    {
        if (count is not { Kind: CountKind.Mechanical } mechanical || rating is null)
            return null;

        double ratio = (double)mechanical.Value / rating.Cycles;
        var zone = GetZone(ratio);
        return mechanical.HasRange && GetZone((double)mechanical.LowerBound / rating.Cycles) != zone
            ? null : new(ratio, zone);
    }

    private static UsageZone GetZone(double ratio) => ratio switch
    {
        < .5 => UsageZone.Low,
        < .8 => UsageZone.Elevated,
        _ => UsageZone.High
    };
}
