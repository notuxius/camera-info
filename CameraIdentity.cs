using System.Text.RegularExpressions;

namespace CameraInspector;

internal static partial class CameraIdentity
{
    private static readonly (string Maker, Regex Model)[] CameraFamilies =
    [
        ("Nikon", new(@"^(?:NIKON (?:DSC )?)?(?:D\d|Z\s*(?:\d|F[C]?\b|R\b)|COOLPIX\b)", RegexOptions.IgnoreCase)),
        ("Fujifilm", new(@"^(?:FUJIFILM )?(?:X-[A-Z]+\d|X\d|X HALF\b|GFX ?\d|GFX ETERNA |FinePix )", RegexOptions.IgnoreCase)),
        ("Sony", new(@"^(?:SONY )?(?:ILC[AE]-|ILME-|ILX-|DSC-|NEX-|SLT-|ZV-|ALPHA |α)", RegexOptions.IgnoreCase)),
        ("Panasonic", new(@"^(?:PANASONIC )?(?:LUMIX\b|(?:DC|DMC)-[A-Z]+\d)", RegexOptions.IgnoreCase)),
        ("OM System", new(@"^(?:OM SYSTEM )?OM-[135](?:\b|MARK)", RegexOptions.IgnoreCase)),
        ("Olympus", new(@"^(?:OLYMPUS |OM-D )?(?:E-M\d|E-P(?:L|M)?\d|PEN-F\b|TG-\d)", RegexOptions.IgnoreCase)),
        ("Pentax", new(@"^(?:PENTAX )?(?:K-?\d|K-?[FPSX]\b|Optio )", RegexOptions.IgnoreCase)),
        ("Ricoh", new(@"^(?:RICOH GR\b|(?:RICOH )?(?:GR ?(?:DIGITAL|I|II|III|IV|IIIx)\b|WG-\d))", RegexOptions.IgnoreCase)),
        ("Leica", new(@"^(?:LEICA )?(?:SL\d?|Q\d?|M\d+|M EV1|[DV]-(?:LUX|Lux)|D-LUX)(?:\b|$)", RegexOptions.IgnoreCase)),
        ("Hasselblad", new(@"^(?:HASSELBLAD )?(?:X[12]D\b|907X\b|CFV\b|H[3456]D\b)", RegexOptions.IgnoreCase))
    ];

    internal static string? MakerForUsb(string id, string? name)
    {
        var usb = UsbId().Match(id);
        if (usb.Success && usb.Groups[1].Value.Equals("04a9", StringComparison.OrdinalIgnoreCase))
            return "Canon";
        if (name is not null)
        {
            if (!usb.Success && name.StartsWith("Canon ", StringComparison.OrdinalIgnoreCase)
                && (name.Contains("EOS", StringComparison.OrdinalIgnoreCase) || name.Contains("PowerShot", StringComparison.OrdinalIgnoreCase)))
                return "Canon";
            foreach (var (maker, model) in CameraFamilies)
                if (model.IsMatch(name))
                    return maker;
        }

        // These PTP IDs help when Windows uses a generic friendly name. Fujifilm can reuse product IDs across models.
        return usb.Value.ToLowerInvariant() switch
        {
            "vid_04cb&pid_0313" => "Fujifilm",
            "vid_04b0&pid_0442" or "vid_04b0&pid_0443" or "vid_04b0&pid_0444"
                or "vid_04b0&pid_0448" or "vid_04b0&pid_044b" or "vid_04b0&pid_044c"
                or "vid_04b0&pid_044f" or "vid_04b0&pid_0450" or "vid_04b0&pid_0451"
                or "vid_04b0&pid_0452" or "vid_04b0&pid_0453" or "vid_04b0&pid_0454"
                or "vid_04b0&pid_0455" => "Nikon",
            "vid_054c&pid_0f50" or "vid_054c&pid_0f56" => "Sony",
            "vid_25fb&pid_2123" => "Ricoh",
            "vid_25fb&pid_018f" => "Pentax",
            _ => null
        };
    }

    internal static string UsbVendor(string id) => UsbId().Match(id).Groups[1].Value.ToLowerInvariant();

    [GeneratedRegex(@"vid_([0-9a-f]{4})&pid_[0-9a-f]{4}(?![0-9a-f])", RegexOptions.IgnoreCase)]
    private static partial Regex UsbId();
}
