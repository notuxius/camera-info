# Backlog

See the [brand support table](README.md#brand-support) for the full model list and current support status.

Start live Nikon DSLR validation with the D750 and D850. For newer readers, start with Nikon Z 5II and Z 6III, Sony α6700 and α7 V, and Fujifilm X-E5 and X-T30 III.

| Priority | Brand or USB route | Next step |
| --- | --- | --- |
| — | **Canon EOS:** legacy property | Compare USB reads with known counts on Windows. |
| — | **Canon EOS:** diagnostics | Validate high counts on Windows and compare the 80D reading with a known count. |
| — | **Canon EOS:** release event | Check live readings around 1,000-actuation boundaries. |
| — | **Canon PowerShot:** G7 X | Verify metadata on Windows. |
| 1 | **Nikon DSLR** | Verify USB metadata on Windows hardware. Check how mechanical and electronic captures advance the separate mechanical field on the D780, D850, and D6. Look for a comparable live USB counter in Nikon’s SDK. [NEF test data](https://shutteractuations.com/nikon-nef-shutter-count-research/) · [Nikon SDK](https://sdk.nikonimaging.com/information/en/) |
| 1 | **Nikon Z** | Validate NEF counter behavior after mechanical and electronic captures. Test USB metadata and look for a live counter in Nikon’s SDK. [Nikon SDK](https://sdk.nikonimaging.com/information/en/) |
| 1 | **Sony Alpha** | Compare ARW counters before and after mechanical, electronic, and electronic-front-curtain captures. Test USB metadata and investigate additional counter layouts. [Sony Remote Toolkit](https://pro.sony/en_GB/digital-imaging/camera-remote-toolkit) · [ExifTool Sony decoder](https://github.com/exiftool/exiftool/blob/master/lib/Image/ExifTool/Sony.pm) |
| 1 | **Fujifilm X** | Verify RAF image-count behavior and compare it with the separate mechanical reading in XApp. Verify USB metadata and investigate live counters. [XApp compatibility](https://www.fujifilm-x.com/global/support/compatibility/software/xapp/) · [X-T5 mechanical counter](https://www.fujifilm-x.com/en-us/support/download/firmware/cameras/x-t5/) |
| 2 | **Pentax and Ricoh** | Verify Pentax PEF and DNG count behavior for stills, Live View, and video. Validate USB metadata and investigate counters on Ricoh GR bodies. [Pentax metadata](https://exiftool.org/TagNames/Pentax.html) · [Ricoh metadata](https://exiftool.org/TagNames/Ricoh.html) |
| 2 | **Panasonic Lumix** | Test USB identity and lookup on the Lumix Tether models. Check whether each body has a readable mechanical counter or an on-camera service reading. Label electronic-only bodies accordingly. [Lumix Tether matrix](https://av.jpn.support.panasonic.com/support/global/cs/soft/download/d_lumixtether2.html) |
| 2 | **OM System and Olympus** | Start with the OM-1 generation and E-M1 Mark II/III. Check USB metadata and compare candidate counters with service or on-camera readings. [OM Capture compatibility](https://software.omsystem.com/omcapture/en/) |
| 3 | **Fujifilm GFX, Leica, and Hasselblad** | Validate original-file and USB sources separately. For leaf-shutter systems, track whether a value belongs to the lens, the body, or both. [Fujifilm tether compatibility](https://www.fujifilm-x.com/zh-cn/support/compatibility/software/tether-app/) · [ExifTool camera samples](https://exiftool.org/models.html) · [Hasselblad system comparison](https://master.hasselblad.com/products/compare-x1d-907x/) |

## Implementation order

1. Complete the Canon Windows hardware matrix. Record body, firmware, USB mode, operation result, and comparison count. Include bodies near a 1,000-shot boundary and diagnostic bodies above 65,535.
2. Validate more original RAW files and add counter layouts where their meaning is known.
3. Add USB reads by brand in priority order. Verify the count against a separate reading and observe which fields advance after controlled mechanical and electronic captures. Add model and firmware fixtures for both successful and unavailable results.
4. Update the support table with hardware results as checks pass. Track device identity, direct USB counts, original-file counts, precision, and available manufacturer durability ratings for each model.
