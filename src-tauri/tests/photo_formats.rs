
use camera_inspector::{camera::{CountKind, Reading, Source}, photo};
use std::{fs, io};

const NOTE: usize = 0x180;

fn photo_data(maker: &str, model: &str, software: &str) -> Vec<u8> {
    let mut data = vec![0u8; 0x280];
    data[..8].copy_from_slice(b"II\x2a\0\x08\0\0\0");
    short(&mut data, 8, 5);
    text(&mut data, 10, 0x010f, 0x48, maker);
    text(&mut data, 22, 0x0110, 0x68, model);
    text(&mut data, 34, 0x0131, 0xa0, software);
    tag(&mut data, 46, 0x8769, 4, 1, 0x100);
    short(&mut data, 0x100, 1);
    tag(&mut data, 0x102, 0x927c, 7, 0x100, NOTE as u32);
    data
}

fn short(bytes: &mut [u8], offset: usize, value: u16) {
    bytes[offset..offset + 2].copy_from_slice(&value.to_le_bytes());
}

fn long(bytes: &mut [u8], offset: usize, value: u32) {
    bytes[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
}

fn tag(bytes: &mut [u8], offset: usize, id: u16, kind: u16, length: u32, value: u32) {
    short(bytes, offset, id);
    short(bytes, offset + 2, kind);
    long(bytes, offset + 4, length);
    long(bytes, offset + 8, value);
}

fn text(bytes: &mut [u8], offset: usize, id: u16, dest: usize, value: &str) {
    let value = value.as_bytes();
    tag(bytes, offset, id, 2, (value.len() + 1) as u32, dest as u32);
    bytes[dest..dest + value.len()].copy_from_slice(value);
}

fn jpeg(data: &[u8]) -> Vec<u8> {
    let mut file = vec![0xff, 0xd8, 0xff, 0xe1];
    file.extend((data.len() as u16 + 8).to_be_bytes());
    file.extend(b"Exif\0\0");
    file.extend(data);
    file
}

fn read(extension: &str, data: &[u8]) -> io::Result<Reading> {
    let temp = tempfile::tempdir()?;
    let path = temp.path().join(format!("camera.{extension}"));
    fs::write(&path, data)?;
    photo::read(&path)
}

#[test]
fn nikon_nef_and_jpeg_handle_relative_makernotes_and_electronic_bodies() {
    for (model, wanted, kind) in [
        ("Z5_2C", "Nikon Z 5IIC", CountKind::Mechanical),
        ("Z 9", "Nikon Z 9", CountKind::Unspecified),
    ] {
        let mut data = photo_data("NIKON CORPORATION", &format!("NIKON {model}"), "Ver.1.23");
        data[NOTE..NOTE + 10].copy_from_slice(b"Nikon\0\x02\x10\0\0");
        let start = NOTE + 10;
        data[start..start + 8].copy_from_slice(b"MM\0\x2a\0\0\0\x08");
        data[start + 8..start + 10].copy_from_slice(&2u16.to_be_bytes());
        for (offset, id, value) in [(start + 10, 0x0037u16, 300u32), (start + 22, 0x00a7, 400)] {
            data[offset..offset + 2].copy_from_slice(&id.to_be_bytes());
            data[offset + 2..offset + 4].copy_from_slice(&4u16.to_be_bytes());
            data[offset + 4..offset + 8].copy_from_slice(&1u32.to_be_bytes());
            data[offset + 8..offset + 12].copy_from_slice(&value.to_be_bytes());
        }
        for reading in [read("NEF", &data).unwrap(), read("jpeg", &jpeg(&data)).unwrap()] {
            assert_eq!(reading.model, wanted);
            assert_eq!(reading.firmware.as_deref(), Some("1.23"));
            assert_eq!(reading.count.unwrap().kind, kind);
        }
    }
}

#[test]
fn fuji_raf_uses_image_counts_and_bounds_preview_reads() {
    let mut data = photo_data("FUJIFILM", "X-E5", "Digital Camera X-E5 Ver1.10");
    data[NOTE..NOTE + 12].copy_from_slice(b"FUJIFILM\x0c\0\0\0");
    short(&mut data, NOTE + 12, 1);
    tag(&mut data, NOTE + 14, 0x1438, 3, 1, 0x80c7);
    let preview = jpeg(&data);
    let mut raf = vec![0; 116];
    raf[..16].copy_from_slice(b"FUJIFILMCCD-RAW ");
    raf[84..88].copy_from_slice(&116u32.to_be_bytes());
    raf[88..92].copy_from_slice(&(preview.len() as u32).to_be_bytes());
    raf.extend(&preview);
    let reading = read("RAF", &raf).unwrap();
    assert_eq!(reading.count.unwrap().value, 199);
    assert_eq!(reading.count.unwrap().kind, CountKind::Image);
    assert_eq!(reading.firmware.as_deref(), Some("1.10"));
    assert!(read("raf", &raf[..128]).is_err());
    assert!(read("jpg", &preview).is_err());
}

#[test]
fn sony_photo_counter_requires_the_mechanical_layout() {
    let mut data = photo_data("SONY", "ILCE-7M5", "ILCE-7M5 v1.00");
    short(&mut data, NOTE, 1);
    tag(&mut data, NOTE + 2, 0x9050, 7, 16, 0x200);
    data[0x20a] = (155usize.pow(3) % 249) as u8;
    data[0x20b] = 8;
    let reading = read("ARW", &data).unwrap();
    assert_eq!(reading.count.unwrap().value, 667);
    assert_eq!(reading.count.unwrap().kind, CountKind::Mechanical);
    let mut with_header = data.clone();
    with_header.copy_within(NOTE..NOTE + 14, NOTE + 12);
    with_header[NOTE..NOTE + 12].copy_from_slice(b"SONY DSC \0\0\0");
    assert_eq!(read("jpg", &jpeg(&with_header)).unwrap().count.unwrap().value, 667);
    data[0x200] = 1;
    assert!(read("arw", &data).unwrap().count.is_none());
}

#[test]
fn pentax_pef_and_dng_keep_the_reported_counter() {
    let mut data = photo_data("RICOH IMAGING COMPANY, LTD.", "PENTAX K-3 Mark III Monochrome", "PENTAX K-3 Mark III Monochrome Ver. 2.10");
    data[NOTE..NOTE + 10].copy_from_slice(b"PENTAX \0II");
    short(&mut data, NOTE + 10, 3);
    tag(&mut data, NOTE + 12, 0x0006, 7, 4, 0x040ce707);
    tag(&mut data, NOTE + 24, 0x0007, 7, 3, 0x00101509);
    tag(&mut data, NOTE + 36, 0x005d, 7, 4, 0x14e20df1);
    let pef = read("PEF", &data).unwrap();
    short(&mut data, 0x100, 0);
    tag(&mut data, 58, 0xc634, 1, 0x100, NOTE as u32);
    let dng = read("DNG", &data).unwrap();
    assert_eq!(pef.source, Source::Pef);
    assert_eq!(dng.source, Source::Dng);
    for reading in [pef, dng] {
        assert_eq!(reading.count.unwrap().value, 495);
        assert_eq!(reading.count.unwrap().kind, CountKind::Unspecified);
    }
}
