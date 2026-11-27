# Backlog

See the [brand support table](README.md#brand-support) for the full model list and current support status.

Start with Nikon D750, D850, Z 6II, and Z 7II, Sony α6700, and Fujifilm X-T5 and X-S20. Add Sony α7 III/IV and Fujifilm X-T3/X-T4 early in validation because they show up frequently in used-camera data. The wider list gives us models to test as the first readers prove out. [MPB used-camera data](https://www.mpb.com/en-us/content/latest-from-mpb/2023-june-september-top-10-most-popular-camera-bodies-lenses) · [MPB 2025 recap](https://www.linkedin.com/pulse/biggest-winners-2025-mpbcom-zyfte)

| Priority | Brand or USB route | Next step |
| --- | --- | --- |
| — | **Canon EOS:** legacy property | Compare USB reads with known counts on Windows. |
| — | **Canon EOS:** diagnostics | Validate high counts on Windows and compare the 80D reading with a known count. |
| — | **Canon EOS:** release event | Check live readings around 1,000-actuation boundaries. |
| — | **Canon PowerShot:** G7 X | Verify metadata on Windows. |
| 1 | **Nikon DSLR** | Read original NEFs locally. Validate the separate mechanical field on D780, D850, and D6 and the shutter-release field on earlier bodies. Compare with the Windows USB/SDK results where available. [NEF test data](https://shutteractuations.com/nikon-nef-shutter-count-research/) · [Nikon durability ratings](https://www.nikonimgsupport.com/eu/BV_article?articleNo=000044853&configured=1&lang=en_GB) |
| 1 | **Nikon Z** | Use a dedicated mechanical field where verified and test Nikon’s USB SDK separately. Show total captures on electronic-only Z 8 and Z 9. [Nikon SDK](https://sdk.nikonimaging.com/information/en/) · [Nikon shutter types](https://www.nikonusa.com/learn-and-explore/c/products-and-innovation/dslr-to-mirrorless-why-should-i-switch) |
| 1 | **Sony Alpha** | Begin with original α6700 ARWs. Compare each counter before and after mechanical, electronic, and electronic-front-curtain captures. Test USB metadata through Sony’s SDK. The α9 III belongs in electronic-only reporting. [Sony Remote Toolkit](https://pro.sony/en_GB/digital-imaging/camera-remote-toolkit) · [ExifTool Sony decoder](https://github.com/exiftool/exiftool/blob/master/lib/Image/ExifTool/Sony.pm) |
| 1 | **Fujifilm X** | Use the XApp mechanical reading as the comparison for supported bodies. Verify what each USB counter measures. Label RAF `ImageCount` as image count when that is the available field. [XApp compatibility](https://www.fujifilm-x.com/global/support/compatibility/software/xapp/) · [X-T5 mechanical counter](https://www.fujifilm-x.com/en-us/support/download/firmware/cameras/x-t5/) · [Fujifilm metadata](https://exiftool.org/TagNames/FujiFilm.html) |
| 2 | **Pentax and Ricoh** | Test original PEF/DNG/JPEG files per body. Record stills, Live View, and video scope. Test USB identity and the option to select an original photo from the camera. [Pentax metadata](https://exiftool.org/TagNames/Pentax.html) · [Ricoh metadata](https://exiftool.org/TagNames/Ricoh.html) |
| 2 | **Panasonic Lumix** | Test USB identity and lookup on the Lumix Tether models. Check whether each body has a readable mechanical counter or an on-camera service reading. Label electronic-only bodies accordingly. [Lumix Tether matrix](https://av.jpn.support.panasonic.com/support/global/cs/soft/download/d_lumixtether2.html) |
| 2 | **OM System and Olympus** | Start with the OM-1 generation and E-M1 Mark II/III. Check USB metadata and compare candidate counters with service or on-camera readings. [OM Capture compatibility](https://software.omsystem.com/omcapture/en/) |
| 3 | **Fujifilm GFX, Leica, and Hasselblad** | Validate original-file and USB sources separately. For leaf-shutter systems, track whether a value belongs to the lens, the body, or both. [Fujifilm tether compatibility](https://www.fujifilm-x.com/zh-cn/support/compatibility/software/tether-app/) · [ExifTool camera samples](https://exiftool.org/models.html) · [Hasselblad system comparison](https://master.hasselblad.com/products/compare-x1d-907x/) |

## Implementation order

1. Complete the Canon Windows hardware matrix. Record body, firmware, USB mode, operation result, and comparison count. Include bodies near a 1,000-shot boundary and diagnostic bodies above 65,535.
2. Add local original-photo import alongside USB discovery. Keep the camera, source, counter meaning, and precision attached to the reading and its history. Start with Nikon NEF and verified Sony ARW fields, then add Pentax/Ricoh and Fujifilm.
3. Add USB reads by brand in priority order. Verify the count against a separate reading and observe which fields advance after controlled mechanical and electronic captures. Add model and firmware fixtures for both successful and unavailable results.
4. Update the support table with hardware results as checks pass. Track device identity, direct USB counts, original-file counts, precision, and available manufacturer durability ratings for each model.
