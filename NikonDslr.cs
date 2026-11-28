using System.Globalization;

namespace CameraInspector;

internal static class NikonDslr
{
    // PTP USB product IDs from libgphoto2's camera table.
    private static readonly (ushort Product, string Model, uint? Cycles)[] Models =
    [
        (0x0428, "D7000", 150_000),
        (0x042A, "D800", 200_000),
        (0x0430, "D7100", 150_000),
        (0x0434, "D610", 150_000),
        (0x0436, "D810", 200_000),
        (0x0437, "D750", 150_000),
        (0x0439, "D7200", 150_000),
        (0x043A, "D5", 400_000),
        (0x043C, "D500", 200_000),
        (0x043F, "D5600", 100_000),
        (0x0440, "D7500", 150_000),
        (0x0441, "D850", 200_000),
        (0x0445, "D3500", null),
        (0x0446, "D780", 150_000),
        (0x0447, "D6", 400_000)
    ];

    private static string? UsbModel(string id)
    {
        const string prefix = "vid_04b0&pid_";
        int index = id.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || id.Length < index + prefix.Length + 4 ||
            !ushort.TryParse(id.AsSpan(index + prefix.Length, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort product))
            return null;

        foreach (var item in Models)
            if (item.Product == product)
                return "Nikon " + item.Model;
        return null;
    }

    internal static string? ConnectedModel(string id, string? name) =>
        UsbModel(id) ?? (name?.StartsWith("Nikon ", StringComparison.OrdinalIgnoreCase) == true ? ModelName(name) : null);

    internal static string? ModelName(string? name)
    {
        string model = name?.Trim() ?? "";
        if (model.StartsWith("Nikon ", StringComparison.OrdinalIgnoreCase))
            model = model[6..].TrimStart();
        if (model.StartsWith("DSC ", StringComparison.OrdinalIgnoreCase))
            model = model[4..].TrimStart();

        foreach (var item in Models)
            if (string.Equals(model, item.Model, StringComparison.OrdinalIgnoreCase))
                return "Nikon " + item.Model;
        return null;
    }

    internal static CameraRating? RatingFor(string model)
    {
        if (!model.StartsWith("Nikon ", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var item in Models)
            if (model.AsSpan(6).Equals(item.Model.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return item.Cycles is uint cycles ? new(cycles, CameraRatings.NikonRatingSource) : null;
        return null;
    }
}
