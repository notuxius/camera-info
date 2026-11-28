namespace CameraInspector;

internal sealed record DesktopCompanionApp(string Name, string? Windows, string? MacOS)
{
    internal DesktopCompanionApp(string name, string downloadPage) : this(name, downloadPage, downloadPage) { }

    internal (string? DownloadUrl, string Platform) ForPlatform(bool isMacOS)
    {
        if (isMacOS)
            return MacOS is null ? (null, "Windows") : (MacOS, "macOS");
        return Windows is null ? (null, "macOS") : (Windows, "Windows");
    }
}

internal static partial class CompanionApps
{
    private const string FujiDownloads = "https://www.fujifilm-x.com/global/support/download/software/";
    private const string NikonDownloads = "https://downloadcenter.nikonimglib.com/en/download/sw/";
    private const string RicohDownloads = "https://www.ricoh-imaging.co.jp/english/support/digital/";
    private const string LumixDownloads = "https://av.jpn.support.panasonic.com/support/global/cs/soft/download/";
    private const string FujiTetherModels = "GFX100II GFX100 GFX100SII GFX100S GFX50SII GFX50S GFX50R GFX100RF " +
        "XH2S XH2 XH1 XPRO3 XPRO2 XT5 XT4 XT3 XT2 XS20 XS10 XT50 XT30III XT30II XT30 XT20 XE5 XE4 XE3 XM5 X100VI X100V X100F";
    private const string NikonZModels = "Z9 Z8 Z6III Z7II Z6II Z7 Z6 Z5IIC Z5II Z5 ZF Z50II Z50 Z30 ZFC ZR";
    private const string LumixTetherModels = "BS1H S1 S1R S1M2 S1M2E S1M2ES S1RM2 S1H S5 S5M2 S5M2X S9 BGH1 GH5 GH5S GH5M2 G9 G9M2 GH6 GH7";
    private const string CanonRModels = "EOSR EOSRA EOSRP EOSR1 EOSR3 EOSR5 EOSR5C EOSR5MARKII EOSR6 EOSR6MARKII EOSR6MARKIII " +
        "EOSR6V EOSR7 EOSR8 EOSR8MARKII EOSR10 EOSR50 EOSR50V EOSR100";
    private const string CanonUtilityModels = CanonRModels + " EOSM6MARKII EOSM6 EOSM5 EOSM3 EOSM10 EOSM200 EOSM100 EOSM50MARKII EOSM50 " +
        "EOS1DXMARKIII EOS1DXMARKII EOS1DX EOS1DC EOS5DS EOS5DSR EOS5DMARKIV EOS5DMARKIII " +
        "EOS6DMARKII EOS6D EOS7DMARKII EOS90D EOS80D EOS77D EOS760D EOS850D EOS800D EOS250D EOS200DII EOS200D EOS750D EOS2000D EOS1500D EOS1300D EOSC50";
    private const string SonyWebcamModels = "ZVE1 ZVE10M2 ZVE10 ILCE1M2 ILCE1 ILCE9M3 ILCE9M2 ILCE9 ILCE7SM3 ILCE7SM2 ILCE7S " +
        "ILCE7RM5 ILCE7RM4A ILCE7RM4 ILCE7RM3A ILCE7RM3 ILCE7RM2 ILCE7CR ILCE7CM2 ILCE7C ILCE7M5 ILCE7M4 ILCE7M3 ILCE7M2 " +
        "ILCE6700 ILCE6600 ILCE6500 ILCE6400A ILCE6400 ILCE6300 ILCE6100A ILCE6100 ILCE5100 ILMEFX3A ILMEFX3 ILMEFX2 ILMEFX30 " +
        "ZV1F ZV1M2 ZV1A ZV1 DSCRX1RM3 DSCRX1RM2 DSCRX10M4 DSCRX10M3 DSCRX10M2 DSCRX100M7A DSCRX100M7 DSCRX100M6 " +
        "DSCRX100M5A DSCRX100M5 DSCRX100M4 DSCRX0M2 DSCRX0 DSCHX99 DSCWX800 DSCWX700";

    private static readonly Dictionary<string, (DesktopCompanionApp App, HashSet<string> Models)[]> DesktopCatalog = new()
    {
        ["CANON"] =
        [
            Rule(new("EOS Utility", "https://app.ssw.imaging-saas.canon/app/en/eu.html"), CanonUtilityModels),
            Rule(new("Digital Photo Professional", "https://app.ssw.imaging-saas.canon/app/en/dpp.html"),
                CanonUtilityModels + " EOS5DMARKII EOS5D EOS7D EOS70D EOS60D EOSM EOSM2 EOS4000D EOS3000D " +
                "POWERSHOTV1 POWERSHOTG7X POWERSHOTG7XMARKII POWERSHOTG7XMARKIII POWERSHOTG5X POWERSHOTG5XMARKII POWERSHOTSX70HS"),
            Rule(new("EOS Webcam Utility Pro", "https://www.usa.canon.com/digital-cameras/eos-webcam-utility/compatible-cameras"),
                "EOSR EOSRA EOSRP EOSR1 EOSR3 EOSR5 EOSR5MARKII EOSR6 EOSR6MARKII EOSR6MARKIII EOSR7 EOSR8 EOSR10 EOSR50 EOSR50V EOSR100 " +
                "EOS1DXMARKII EOS1DXMARKIII EOS5DMARKIV EOS5DS EOS5DSR EOS6D EOS6DMARKII EOS7DMARKII EOS77D EOS80D EOS90D " +
                "EOSM200 EOSM50 EOSM50MARKII EOSM6MARKII EOS850D EOS800D EOS760D EOS750D EOS1300D EOS2000D EOS4000D EOS200D EOS250D " +
                "POWERSHOTSX70HS POWERSHOTG5XMARKII POWERSHOTG7XMARKIII POWERSHOTV1 POWERSHOTV10"),
            Rule(new("ShutterCheck", null, "https://shuttercheck.app/specs/"),
                "EOS1DMARKIV EOS1DX EOS1DXMARKII EOS1DXMARKIII EOS1DC EOS5DS EOS5DSR EOS5DMARKII EOS5DMARKIII EOS5DMARKIV " +
                "EOS6D EOS6DMARKII EOS7D EOS7DMARKII EOS40D EOS50D EOS50DRETINA EOS60D EOS60DRETINA EOS60DA EOS70D EOS77D EOS770D EOS9000D " +
                "EOS80D EOS80DRETINA EOS90D EOS100D EOS200D EOS200DII EOS250D EOS450D EOS500D EOS550D EOS600D EOS650D EOS700D EOS750D " +
                "EOS760D EOS8000D EOS800D EOS850D EOS1000D EOS1100D EOS1200D EOS1300D EOS1500D EOS2000D EOS3000D EOS4000D " +
                "EOSR EOSRA EOSRP EOSR3 EOSR5 EOSR5C EOSR6 EOSR6MARKII EOSR7 EOSR8 EOSR10 EOSR50 EOSR100 EOSM6MARKII EOSM50 EOSM50MARKII EOSM200 " +
                "POWERSHOTG5XMARKII POWERSHOTG7XMARKIII POWERSHOTSX70HS POWERSHOTSX740HS")
        ],
        ["NIKON"] =
        [
            Rule(new("NX Studio", NikonDownloads + "279.html"), NikonZModels +
                " D1 D1H D1X D2H D2HS D2X D2XS D3 D3S D3X D4 D4S D5 D6 D40 D40X D50 D60 D70 D70S D80 D90 D100 D200 D300 D300S D500 " +
                "D600 D610 D700 D750 D780 D800 D800E D810 D810A D850 DF D3000 D3100 D3200 D3300 D3400 D3500 D5000 D5100 D5200 D5300 D5500 D5600 " +
                "D7000 D7100 D7200 D7500 COOLPIXP1000 COOLPIXP1100 COOLPIXP950 COOLPIXA1000 COOLPIXW300 KEYMISSION360 KEYMISSION170 KEYMISSION80"),
            Rule(new("NX Tether", NikonDownloads + "278.html"), NikonZModels + " D6 D780"),
            Rule(new("Nikon Webcam Utility", NikonDownloads + "220.html", NikonDownloads + "269.html"),
                "Z9 Z8 Z7II Z7 ZF Z6III Z6II Z6 Z5 ZFC Z50 Z30 D6 D5 D850 D810 D780 D750 D500 D7500 D7200 D5600 D5500 D5300 D3500"),
            Rule(new("Wireless Transmitter Utility", NikonDownloads + "277.html"), NikonZModels + " D6 D780")
        ],
        ["SONY"] =
        [
            Rule(new("Imaging Edge Desktop", "https://support.d-imaging.sony.co.jp/app/imagingedge/en/"),
                SonyWebcamModels + " ILCE7RM6 ILCE7R ILCE7 ILCE6000 ILCE5000 ILCE3000"),
            Rule(new("Imaging Edge Webcam", "https://support.d-imaging.sony.co.jp/app/webcam/en/download/"), SonyWebcamModels),
            Rule(new("Catalyst Browse", "https://www.sony.net/catalyst/"),
                "ILMEFX2 ILMEFX3 ILMEFX3A ILMEFX30")
        ],
        ["FUJIFILM"] =
        [
            Rule(new("FUJIFILM X RAW STUDIO", FujiDownloads + "x-raw-studio/"), FujiTetherModels),
            Rule(new("FUJIFILM TETHER APP", FujiDownloads + "tether-app/"), FujiTetherModels + " XT1"),
            Rule(new("FUJIFILM Pixel Shift Combiner", FujiDownloads + "pixel-shift-combiner/"),
                "GFX100II GFX100 GFX100SII GFX100S GFX50SII XH2 XT5"),
            Rule(new("FUJIFILM X Webcam", "https://www.fujifilm-x.com/global/products/software/x-webcam/"),
                "GFX100 GFX100S GFX50SII GFX50S GFX50R XH1 XPRO3 XPRO2 XT4 XT3 XT2 XS10 XT30II XT30 XE4 X100V")
        ],
        ["PANASONIC"] =
        [
            Rule(new("LUMIX Tether", LumixDownloads + "d_lumixtether.html"), LumixTetherModels),
            Rule(new("LUMIX Webcam Software", LumixDownloads + "lumix_webcam.html"),
                "BS1H S1 S1R S1H S5 S5M2 S5M2X S9 BGH1 GH5 GH5S GH5M2 G9 G9M2 GH6 GH7 G97 G100 G110")
        ],
        ["OMSYSTEM"] = OmDesktopApps(),
        ["OLYMPUS"] = OmDesktopApps(),
        ["PENTAX"] =
        [
            Rule(new("Digital Camera Utility 5", RicohDownloads + "dc_utility5_win.html", RicohDownloads + "dc_utility5_mac.html"),
                "K3 K3II K3MARKII K3MARKIII K3MARKIIIMONOCHROME KS2 KS1 645Z QS1 K1 K70 KP K1MARKII KF"),
            Rule(new("IMAGE Transmitter 2", "https://www.ricoh-imaging.co.jp/english/products/imagetransmitter2/"), "K1 K1MARKII K3MARKIII KP 645Z 645ZIR"),
            Rule(new("IMAGE Transmitter 2", RicohDownloads + "image_transmitter2_win.html", null), "K3MARKIIIMONOCHROME")
        ],
        ["RICOH"] =
        [
            Rule(new("Digital Camera Utility 5", RicohDownloads + "dc_utility5_win.html", RicohDownloads + "dc_utility5_mac.html"), "GRII"),
            Rule(new("IMAGE Transmitter 2", "https://www.ricoh-imaging.co.jp/english/products/imagetransmitter2/"), "G900SE")
        ],
        ["LEICA"] =
        [
            Rule(new("Adobe Lightroom Classic", "https://helpx.adobe.com/lightroom-classic/desktop/kb/tethered-camera-support.html#leica"),
                "SL3P SL3 SL3S Q3 Q343 Q3MONOCHROM Q343MONOCHROM SLTYP601 SL2 SL2S S S3 M11 M11MONOCHROM M11P MEV1")
        ],
        ["HASSELBLAD"] =
        [
            Rule(new("Phocus", "https://www.hasselblad.com/phocus/phocus-for-pc-mac/"),
                "X1D50C X1DII50C X2D100C X2DII100C CFV100C CFVII50C 907XCFV100C 907X&CFV100C 907XCFVII50C 907X&CFVII50C")
        ]
    };

    internal static IReadOnlyList<DesktopCompanionApp> DesktopFor(CameraReading? reading)
    {
        if (reading is null)
            return [];

        var (maker, model) = CameraKey(reading);
        model = maker switch
        {
            "CANON" => model switch { "EOS6DW" or "EOS6DWG" => "EOS6D", "EOS70DW" => "EOS70D", _ => model },
            "PANASONIC" => LumixDesktopModel(model),
            "SONY" => SonyDesktopModel(model),
            _ => model
        };

        return DesktopCatalog.TryGetValue(maker, out var rules)
            ? rules.Where(rule => rule.Models.Contains(model)).Select(rule => rule.App).ToArray()
            : [];
    }

    private static (DesktopCompanionApp App, HashSet<string> Models) Rule(DesktopCompanionApp app, string models) =>
        (app, new HashSet<string>(models.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal));

    private static (DesktopCompanionApp App, HashSet<string> Models)[] OmDesktopApps() =>
    [
        Rule(new("OM Workspace", "https://explore.omsystem.com/us/en/software"),
            "OM1 OM1MARKII OM3 OM5 OM5MARKII EM1 EM1MARKII EM1MARKIII EM1X EM5 EM5MARKII EM5MARKIII EM10 EM10MARKII EM10MARKIII EM10MARKIV PENF TG7 TG6 TG5"),
        Rule(new("OM Capture", "https://software.omsystem.com/omcapture/en/"), "OM1 OM1MARKII EM1X EM1MARKIII EM1MARKII EM1 EM5MARKII")
    ];

    private static string LumixDesktopModel(string model) =>
        RemovePrefix(RemovePrefix(model, "DC"), "DMC") switch
        {
            "S1II" => "S1M2",
            "S1IIE" => "S1M2ES",
            "S1RII" => "S1RM2",
            "S5II" => "S5M2",
            "S5IIX" => "S5M2X",
            "G9II" => "G9M2",
            "GH5II" => "GH5M2",
            var key => key
        };

    private static string SonyDesktopModel(string model) => model switch
    {
        "A1" or "ALPHA1" => "ILCE1",
        "A1II" or "ALPHA1II" => "ILCE1M2",
        "A9" or "ALPHA9" => "ILCE9",
        "A9II" or "ALPHA9II" => "ILCE9M2",
        "A9III" or "ALPHA9III" => "ILCE9M3",
        "A7" or "ALPHA7" => "ILCE7",
        "A7II" or "ALPHA7II" => "ILCE7M2",
        "A7III" or "ALPHA7III" => "ILCE7M3",
        "A7IV" or "ALPHA7IV" => "ILCE7M4",
        "A7V" or "ALPHA7V" => "ILCE7M5",
        "A7R" or "ALPHA7R" => "ILCE7R",
        "A7RII" or "ALPHA7RII" => "ILCE7RM2",
        "A7RIII" or "ALPHA7RIII" => "ILCE7RM3",
        "A7RIV" or "ALPHA7RIV" => "ILCE7RM4",
        "A7RV" or "ALPHA7RV" => "ILCE7RM5",
        "A7RVI" or "ALPHA7RVI" => "ILCE7RM6",
        "A7S" or "ALPHA7S" => "ILCE7S",
        "A7SII" or "ALPHA7SII" => "ILCE7SM2",
        "A7SIII" or "ALPHA7SIII" => "ILCE7SM3",
        "A7C" or "ALPHA7C" => "ILCE7C",
        "A7CII" or "ALPHA7CII" => "ILCE7CM2",
        "A7CR" or "ALPHA7CR" => "ILCE7CR",
        "A6000" or "ALPHA6000" => "ILCE6000",
        "A6100" or "ALPHA6100" => "ILCE6100",
        "A6300" or "ALPHA6300" => "ILCE6300",
        "A6400" or "ALPHA6400" => "ILCE6400",
        "A6500" or "ALPHA6500" => "ILCE6500",
        "A6600" or "ALPHA6600" => "ILCE6600",
        "A6700" or "ALPHA6700" => "ILCE6700",
        "FX2" => "ILMEFX2",
        "FX3" => "ILMEFX3",
        "FX3A" => "ILMEFX3A",
        "FX30" => "ILMEFX30",
        "ZVE10II" => "ZVE10M2",
        _ => model
    };
}
