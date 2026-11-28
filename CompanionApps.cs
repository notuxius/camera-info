namespace CameraInspector;

internal sealed record CompanionApp(string? Name, string? AppStore, string? GooglePlay,
    string? Compatibility = null, bool HasMechanicalCount = false);

internal static class CompanionApps
{
    private const string Apple = "https://apps.apple.com/app/id";
    private const string Google = "https://play.google.com/store/apps/details?id=";

    // https://www.fujifilm-x.com/global/support/compatibility/software/xapp/
    private static readonly CompanionApp XApp = new("FUJIFILM XApp", Apple + "1586089681", Google + "com.fujifilm.xapp");
    private static readonly CompanionApp XAppWithCounter = XApp with { HasMechanicalCount = true };
    private static readonly CompanionApp XT5Firmware = new(XApp.Name, null, null,
        "https://www.fujifilm-x.com/global/support/download/firmware/cameras/x-t5/");
    // https://www.fujifilm-x.com/en-au/support/compatibility/cameras/x-e3/
    private static readonly CompanionApp CameraRemote = new("FUJIFILM Camera Remote", Apple + "793063045", Google + "com.fujifilm_dsc.app.remoteshooter");
    // https://www.fujifilm-x.com/global/support/compatibility/software/x-half-app/
    private static readonly CompanionApp XHalf = new("X half", Apple + "6743934694", Google + "com.fujifilm.x_half");

    // https://app.ssw.imaging-saas.canon/app/en/cc.html
    private static readonly CompanionApp CameraConnect = new("Canon Camera Connect", Apple + "944097177", Google + "jp.co.canon.ic.cameraconnect");

    // https://nikonimglib.com/snbr/onlinehelp/en/compatible_cameras_44.html
    // https://nikonimglib.com/snbr/onlinehelp/en/wmu_36.html
    private static readonly CompanionApp SnapBridge = new("SnapBridge", Apple + "1121563450", Google + "com.nikon.snapbridge.cmru");
    private static readonly CompanionApp SnapBridgeAndroid = SnapBridge with { AppStore = null };

    // https://www.sony.com/electronics/support/a-mount-body-ilca-60-series/articles/CCCA03001
    // https://support.d-imaging.sony.co.jp/app/iemobile/en/instruction/
    private static readonly CompanionApp Creators = new("Creators’ App", Apple + "1616622856", Google + "jp.co.sony.ips.portalapp");
    private static readonly CompanionApp ImagingEdge = new("Imaging Edge Mobile", Apple + "489191124", Google + "com.sony.playmemories.mobile");
    private static readonly CompanionApp SonyFirmware = new(null, null, null,
        "https://www.sony.com/electronics/support/e-mount-body-ilce-7-series/ilce-7m4k/articles/00284526");

    // https://av.jpn.support.panasonic.com/support/global/cs/soft/lumix_lab/en/index.html
    // https://av.jpn.support.panasonic.com/support/global/cs/soft/lumix_sync/en/index.html
    private static readonly CompanionApp LumixLab = new("LUMIX Lab", Apple + "6499262377", Google + "com.panasonic.jp.lumixlab");
    private static readonly CompanionApp LumixSync = new("LUMIX Sync", Apple + "1420406579", Google + "com.panasonic.jp.lumixsync");

    // https://support.jp.omsystem.com/jp/support/cs/qa/content/006345/index.html
    // https://learning.omsystem.com/PEN-F/zz_pdf/pen-f_eng.pdf
    private static readonly CompanionApp ImageShare = new("OM Image Share", Apple + "1574448678", Google + "com.omdigitalsolutions.oishare");

    // https://www.ricoh-imaging.co.jp/english/products/app/index.html
    // https://www.ricoh-imaging.co.jp/english/support/qa/k-3-3mono/index.html
    private static readonly CompanionApp ImageSync = new("Image Sync", Apple + "959773524", Google + "com.ricohimaging.imagesync");
    private static readonly CompanionApp GrWorld = new("GR WORLD", Apple + "6743932022", Google + "com.ricohimaging.grworld");

    // https://leica-camera.com/en-int/photography/leica-apps/leica-fotos
    private static readonly CompanionApp Fotos = new("Leica FOTOS", Apple + "1356061526", Google + "com.leica_camera.app");

    // https://master.hasselblad.com/phocus/phocus-mobile-2/
    private static readonly CompanionApp Phocus = new("Phocus Mobile", Apple + "1452280435", Google + "com.hasselblad.phocus2");
    private static readonly CompanionApp PhocusApple = Phocus with { GooglePlay = null };

    internal static CompanionApp? For(CameraReading? reading)
    {
        if (reading is null)
            return null;

        string manufacturer = reading.Maker.Trim().ToUpperInvariant();
        string maker = manufacturer.Split(' ', 2)[0];
        string model = Key(reading.Model);
        maker = maker switch
        {
            "RICOH" when model.StartsWith("PENTAX", StringComparison.Ordinal) => "PENTAX",
            "OM" when manufacturer is "OM SYSTEM" || manufacturer.StartsWith("OM DIGITAL SOLUTIONS", StringComparison.Ordinal) => "OMSYSTEM",
            _ => maker
        };
        model = maker switch
        {
            "CANON" => CanonShutter.ModelKey(reading.Model),
            "NIKON" => RemovePrefix(Key(NikonNef.ModelName(reading.Model) ?? reading.Model), "NIKON"),
            "PANASONIC" => RemovePrefix(RemovePrefix(model, "PANASONIC"), "LUMIX"),
            "OMSYSTEM" or "OLYMPUS" => RemovePrefix(RemovePrefix(RemovePrefix(model, "OMSYSTEM"), "OLYMPUS"), "OMD"),
            _ => RemovePrefix(model, maker)
        };

        return (maker, model) switch
        {
            ("FUJIFILM", "XE5") => XAppWithCounter,
            ("FUJIFILM", "XT30III" or "XM5" or "XT50" or "X100VI" or "GFX100RF") => XApp,
            // https://www.fujifilm-x.com/global/support/download/firmware/cameras/x-s20/
            ("FUJIFILM", "XS20") => Firmware(reading) >= new Version(2, 1) ? XAppWithCounter : XApp,
            ("FUJIFILM", "XT5") when Firmware(reading) >= new Version(3, 1) => XAppWithCounter,
            ("FUJIFILM", "XT5") => Firmware(reading)?.Major >= 2 ? XApp : XT5Firmware,
            ("FUJIFILM", "XE3") => CameraRemote,
            ("FUJIFILM", "XHALF" or "XHF1") => XHalf,

            ("CANON", "EOSR" or "EOSRP" or "EOSR1" or "EOSR3" or "EOSR5" or "EOSR5C" or "EOSR5MARKII"
                or "EOSR6" or "EOSR6MARKII" or "EOSR6MARKIII" or "EOSR6V" or "EOSR7" or "EOSR8" or "EOSR8MARKII"
                or "EOSR10" or "EOSR50" or "EOSR50V" or "EOSR100" or "EOSM50MARKII" or "EOS850D" or "EOS90D"
                or "EOS80D" or "EOS6DMARKII" or "EOS5DMARKIV" or "EOS7DMARKII" or "EOS70DW" or "EOS6DW" or "EOS6DWG"
                or "POWERSHOTG7X" or "POWERSHOTG7XMARKII" or "POWERSHOTG7XMARKIII" or "POWERSHOTV1") => CameraConnect,

            ("NIKON", "Z5" or "Z5II" or "Z5IIC" or "Z6" or "Z6II" or "Z6III" or "Z7" or "Z7II" or "Z8" or "Z9"
                or "Z30" or "Z50" or "Z50II" or "ZF" or "ZFC" or "ZR"
                or "D3500" or "D5600" or "D7500" or "D500" or "D780" or "D850" or "D6") => SnapBridge,
            ("NIKON", "D750" or "D7100" or "D7200") => SnapBridgeAndroid,

            ("SONY", "ILCE6700" or "A6700" or "ALPHA6700" or "ILCE7CM2" or "A7CII" or "ALPHA7CII"
                or "ILCE7CR" or "A7CR" or "ALPHA7CR" or "ILCE1M2" or "A1II" or "ALPHA1II"
                or "ILCE7M5" or "A7V" or "ALPHA7V" or "ILCE7RM6" or "A7RVI" or "ALPHA7RVI"
                or "ILCE9M3" or "A9III" or "ALPHA9III" or "ILMEFX2" or "FX2" or "ILMEFX3A" or "FX3A" or "ZVE10M2" or "ZVE10II") => Creators,
            ("SONY", "ILCE6100" or "A6100" or "ALPHA6100" or "ILCE6400" or "A6400" or "ALPHA6400"
                or "ILCE6600" or "A6600" or "ALPHA6600" or "ILCE7C" or "A7C" or "ALPHA7C" or "ILCE7M3" or "A7III" or "ALPHA7III"
                or "ILCE7RM3" or "A7RIII" or "ALPHA7RIII" or "ILCE7RM4" or "A7RIV" or "ALPHA7RIV"
                or "ILCE9" or "A9" or "ALPHA9" or "ILCE9M2" or "A9II" or "ALPHA9II" or "ZVE10") => ImagingEdge,
            ("SONY", "ILCE1" or "A1" or "ALPHA1" or "ILCE7M4" or "A7IV" or "ALPHA7IV" or "ILCE7RM5"
                or "A7RV" or "ALPHA7RV" or "ILMEFX30" or "FX30") => SonyApp(reading, 2),
            ("SONY", "ILCE7SM3" or "A7SIII" or "ALPHA7SIII" or "ILMEFX3" or "FX3") => SonyApp(reading, 3),

            ("PANASONIC", "DCS1M2" or "DCS1M2E" or "DCS1M2ES" or "DCS1RM2" or "S1II" or "S1IIE" or "S1RII"
                or "DCS9" or "S9" or "DCGH7" or "GH7") => LumixLab,
            ("PANASONIC", "DCS5M2" or "S5II") => Firmware(reading) >= new Version(3, 1) ? LumixLab : LumixSync,
            ("PANASONIC", "DCS5M2X" or "S5IIX") => Firmware(reading) >= new Version(2, 1) ? LumixLab : LumixSync,
            ("PANASONIC", "DCG9M2" or "G9II") => Firmware(reading) >= new Version(2, 2) ? LumixLab : LumixSync,
            ("PANASONIC", "DCS5" or "S5" or "DCGH6" or "GH6" or "DCGH5M2" or "GH5II" or "DCS1" or "DCS1H" or "DCS1R") => LumixSync,

            ("OMSYSTEM" or "OLYMPUS", "OM1" or "OM1MARKII" or "OM3" or "OM5" or "OM5MARKII"
                or "EM1MARKIII" or "EM1MARKII" or "EM1X" or "PENF" or "TG7") => ImageShare,
            ("PENTAX" or "RICOH", "K3MARKIII" or "K3MARKIIIMONOCHROME" or "KF" or "K1MARKII" or "K1" or "KP" or "K70" or "KS2") => ImageSync,
            ("RICOH", "GRII" or "GRIII" or "GRIIIX" or "GRIV" or "GRIVHDF" or "GRIVMONOCHROME") => GrWorld,
            ("LEICA", "M11" or "MEV1" or "Q3" or "Q343" or "Q3MONOCHROM" or "SL3" or "SL3S") => Fotos,
            ("HASSELBLAD", "X2D100C" or "X2DII100C" or "CFV100C" or "907XCFV100C" or "907X&CFV100C") => Phocus,
            ("HASSELBLAD", "X1DII50C" or "CFVII50C" or "907XCFVII50C" or "907X&CFVII50C") => PhocusApple,
            _ => null
        };
    }

    private static CompanionApp SonyApp(CameraReading reading, int creatorsMajor)
    {
        // An original photo records the firmware at capture time.
        if (reading.Source != ReadingSource.Usb || Firmware(reading) is not { } firmware)
            return SonyFirmware;

        return firmware.Major >= creatorsMajor ? Creators : ImagingEdge;
    }

    private static Version? Firmware(CameraReading reading)
    {
        string version = reading.Firmware?.Trim() ?? "";
        if (version.StartsWith("Ver.", StringComparison.OrdinalIgnoreCase))
            version = version[4..].TrimStart();
        else if (version.StartsWith('v') || version.StartsWith('V'))
            version = version[1..].TrimStart();
        return Version.TryParse(version, out var firmware) && firmware.Major > 0 ? firmware : null;
    }

    private static string RemovePrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;

    private static string Key(string value) => string.Concat(value.ToUpperInvariant()
        .Where(character => !char.IsWhiteSpace(character) && character is not ('-' or '_'))).Replace('Α', 'A');
}
