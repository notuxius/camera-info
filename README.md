# Camera Info

A Windows utility for reading Canon cameras over USB and comparing mechanical shutter usage with Canon’s published durability ratings.

## Download

Download [Camera Info for Windows x64](https://github.com/notuxius/camera-info/releases/latest/download/CameraInfo-win-x64.exe). The EXE requires the [.NET 10 Desktop Runtime for Windows x64](https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe).

## Example dashboard

![Camera Info dashboard showing 87,000 mechanical shutter actuations on a Canon EOS 5D Mark III](docs/dashboard-example.png)

## Use the app

1. Connect the camera to your PC with a USB data cable and switch it on.
2. If the camera asks for a USB mode, choose photo import, remote control, or PC connection.
3. Open Camera Info and select **Scan for cameras**. Close Canon EOS Utility if it is holding the connection.

Use the **Language** menu at the top of the window to switch the interface. Camera Info remembers your selection the next time you open it.

Camera Info displays the model, firmware, serial number, battery charge, and shutter count when the camera supplies them. USB discovery works through Windows Portable Devices. The app displays uncertain counters as reported and leaves the usage status gray. When the camera supplies a range or an uncertain counter, you can enter an exact mechanical count from a service report if a Canon rating is available.

The first USB scan with a serial number saves a baseline on this PC. Later scans compare the same camera model, serial number, and Canon counter. The dashboard shows whether the count changed and remembers the most recent decrease. History is stored under `%LOCALAPPDATA%\Camera Info\Shutter History`.

A decrease can also follow a shutter replacement. To investigate a change from before the first scan, compare with an earlier reading or service report for the same camera and counter. On bodies that report in thousands, a reading of `≤ 12,000` represents 11,001 through 12,000 actuations. A range can stay the same while the shutter is used. A range crossing a usage threshold leaves the zone unassigned.

## Canon USB support

The Windows reader routes the following 22 target models. It also retains the legacy reader for the 5D Mark II and original 7D.

| USB counter | Target models |
| --- | --- |
| Legacy shutter property | EOS 5D Mark III, 6D, 7D Mark II, 70D |
| Canon diagnostic shutter counter | EOS 5D Mark IV, 6D Mark II, 80D |
| EOS release event, in 1,000-actuation steps | EOS 90D, 850D (Rebel T8i / Kiss X10i), M50 Mark II (Kiss M2), R, RP, R5, R5 Mark II, R6, R6 Mark II, R6 Mark III, R7, R8, R10, R50, R100 |

The headless checks emulate all 24 USB model routes at the Windows Portable Devices interface. They cover high counts on all three counter formats, interrupted transfers, read-only access, session cleanup, and how the dashboard and shutter history handle emulated readings. The diagnostic decoder uses both high bytes and the rotating low byte. A published EOS 5DS R capture for the same counter decodes to 189,052 and then 189,072 after 20 shots. An unfamiliar reply layout leaves the mechanical count unavailable. [USB product IDs](https://github.com/gphoto/libgphoto2/blob/master/camlibs/ptp2/library.c) · [EOS R capture](https://github.com/gphoto/libgphoto2/blob/master/camlibs/ptp2/cameras/canon-eos-r.txt) · [EOS R8 field tests](https://github.com/DigitalN/Open-Shutter-Count/blob/main/docs/R8-PROGRESS.md) · [5D Mark IV diagnostics](https://github.com/mvisonneau/openshuttercounter/blob/main/docs/PROTOCOL.md) · [6D Mark II captures](https://github.com/mvisonneau/openshuttercounter/pull/8) · [5DS R high-count captures](https://github.com/mvisonneau/openshuttercounter/pull/5)

The EOS 80D firmware 1.0.1 USB log lists the diagnostic commands, and a separate desktop reader reports successful 80D reads on firmware 1.0.1 through 1.0.3. Camera Info still needs a live Windows check against a known 80D mechanical count. Disable Wi-Fi/NFC and time-lapse movie mode before trying an 80D USB read. [80D USB protocol log](https://github.com/gphoto/libgphoto2/blob/master/camlibs/ptp2/cameras/canon-80d.txt) · [80D firmware test results](https://app.camerashuttercount.com/nb/cameras/canon-eos-80d)

## Shutter usage zones

| Zone | Share of Canon’s rated cycles |
| --- | --- |
| Green · Low | Below 50% |
| Yellow · Elevated | 50% to under 80% |
| Red · High | 80% or higher |

These thresholds are app-defined. The rating measures tested shutter durability and actual service life varies. The app links to Canon’s source when a rating is available.

## Build

Use the .NET 10 SDK to build on Windows.

```powershell
dotnet publish CameraInfo.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o dist/win-x64
```

Run the headless checks with `dotnet run --project tests/CameraInfo.Checks.csproj -c Release`. Pass an output directory after `--` to also render dashboard screenshots without showing a window.

USB transport uses the Windows MTP extensions and Canon’s EOS PTP properties. [Microsoft WPD MTP documentation](https://learn.microsoft.com/en-us/windows/win32/wpd_sdk/supporting-mtp-extensions) · [libgphoto2 property definitions](https://github.com/gphoto/libgphoto2/blob/master/camlibs/ptp2/ptp.h)

## Other-brand support backlog

Start with Nikon D750, D850, Z 6II, and Z 7II, Sony α6700, and Fujifilm X-T5 and X-S20. Add Sony α7 III/IV and Fujifilm X-T3/X-T4 early in validation because they show up frequently in used-camera data. The wider list gives us models to test as the first readers prove out. [MPB used-camera data](https://www.mpb.com/en-us/content/latest-from-mpb/2023-june-september-top-10-most-popular-camera-bodies-lenses) · [MPB 2025 recap](https://www.linkedin.com/pulse/biggest-winners-2025-mpbcom-zyfte)

| Priority | Brand and model candidates | First implementation or research task |
| --- | --- | --- |
| 1 | **Nikon DSLR:** D3500, D5600, D7000, D7100, D7200, D7500, D500, D610, D750, D780, D800, D810, D850, D5, D6 | Read original NEFs locally. Validate the separate mechanical field on D780, D850, and D6 and the shutter-release field on earlier bodies. Compare with the Windows USB/SDK results where available. [NEF test data](https://shutteractuations.com/nikon-nef-shutter-count-research/) · [Nikon durability ratings](https://www.nikonimgsupport.com/eu/BV_article?articleNo=000044853&configured=1&lang=en_GB) |
| 1 | **Nikon Z:** Z 5, Z 5II, Z 6, Z 6II, Z 6III, Z 7, Z 7II, Z 30, Z 50, Z 50II, Z fc, Z f, Z 8, Z 9 | Use a dedicated mechanical field where verified and test Nikon’s USB SDK separately. Show total captures on electronic-only Z 8 and Z 9. [Nikon SDK](https://sdk.nikonimaging.com/information/en/) · [Nikon shutter types](https://www.nikonusa.com/learn-and-explore/c/products-and-innovation/dslr-to-mirrorless-why-should-i-switch) |
| 1 | **Sony Alpha:** α6000, α6100, α6300, α6400, α6500, α6600, α6700, α7 II, α7 III, α7 IV, α7 V, α7C, α7C II, α7CR, α7R III, α7R IV, α7R V, α7S III, α1, α1 II, α9 II, α9 III, ZV-E10 | Begin with original α6700 ARWs. Compare each counter before and after mechanical, electronic, and electronic-front-curtain captures. Test USB metadata through Sony’s SDK. The α9 III belongs in electronic-only reporting. [Sony Remote Toolkit](https://pro.sony/en_GB/digital-imaging/camera-remote-toolkit) · [ExifTool Sony decoder](https://github.com/exiftool/exiftool/blob/master/lib/Image/ExifTool/Sony.pm) |
| 1 | **Fujifilm X:** X-T2, X-T3, X-T4, X-T5, X-T30, X-T30 II, X-T50, X-S10, X-S20, X-H1, X-H2, X-H2S, X-Pro2, X-Pro3, X-E4, X100F, X100V, X100VI | Use the XApp mechanical reading as the comparison for supported bodies. Verify what each USB counter measures. Label RAF `ImageCount` as image count when that is the available field. [XApp compatibility](https://www.fujifilm-x.com/global/support/compatibility/software/xapp/) · [X-T5 mechanical counter](https://www.fujifilm-x.com/en-us/support/download/firmware/cameras/x-t5/) · [Fujifilm metadata](https://exiftool.org/TagNames/FujiFilm.html) |
| 2 | **Pentax and Ricoh:** K-5 II, K-3, K-3 II, K-3 Mark III, K-3 Mark III Monochrome, K-70, KP, KF, K-1, K-1 Mark II, GR II, GR III, GR IIIx, GR IV | Test original PEF/DNG/JPEG files per body. Record stills, Live View, and video scope. Test USB identity and the option to select an original photo from the camera. [Pentax metadata](https://exiftool.org/TagNames/Pentax.html) · [Ricoh metadata](https://exiftool.org/TagNames/Ricoh.html) |
| 2 | **Panasonic Lumix:** S1, S1R, S5, S5II, S5IIX, S9, G9, G9 II, G85/G80, GX85/GX80, GH5, GH5 II, GH6, GH7 | Test USB identity and lookup on the Lumix Tether models. Check whether each body has a readable mechanical counter or an on-camera service reading. Label electronic-only bodies accordingly. [Lumix Tether matrix](https://av.jpn.support.panasonic.com/support/global/cs/soft/download/d_lumixtether2.html) |
| 2 | **OM System and Olympus:** OM-1, OM-1 Mark II, OM-3, OM-5, OM-5 Mark II, E-M1 Mark II, E-M1 Mark III, E-M1X, E-M5 Mark III, E-M10 Mark IV, PEN-F | Start with the OM-1 generation and E-M1 Mark II/III. Check USB metadata and compare candidate counters with service or on-camera readings. [OM Capture compatibility](https://software.omsystem.com/omcapture/en/) |
| 3 | **Fujifilm GFX, Leica, and Hasselblad:** GFX 50S II, GFX 100S, GFX 100S II, GFX 100 II, Leica M10, M11, Q2, Q3, Q3 43, SL2, SL2-S, SL3, Hasselblad X1D II 50C, X2D 100C, X2D II 100C, 907X & CFV 100C | Validate original-file and USB sources separately. For leaf-shutter systems, track whether a value belongs to the lens, the body, or both. [Fujifilm tether compatibility](https://www.fujifilm-x.com/zh-cn/support/compatibility/software/tether-app/) · [ExifTool camera samples](https://exiftool.org/models.html) · [Hasselblad system comparison](https://master.hasselblad.com/products/compare-x1d-907x/) |

### Implementation order

1. Complete the Canon Windows hardware matrix. Record body, firmware, USB mode, operation result, and comparison count. Include bodies near a 1,000-shot boundary and diagnostic bodies above 65,535.
2. Add local original-photo import alongside USB discovery. Keep the camera, source, counter meaning, and precision attached to the reading and its history. Start with Nikon NEF and verified Sony ARW fields, then add Pentax/Ricoh and Fujifilm.
3. Add USB reads by brand in priority order. Verify the count against a separate reading and observe which fields advance after controlled mechanical and electronic captures. Add model and firmware fixtures for both successful and unavailable results.
4. Publish a compatibility matrix after hardware checks pass. Track device identity, direct USB counts, original-file counts, precision, and available manufacturer durability ratings for each model.
