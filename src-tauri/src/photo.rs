
use crate::camera::{invalid, nikon_model, CountKind, Reading, ShutterCount, Source};
use std::{fs::File, io::{self, Read, Seek, SeekFrom}, path::Path};

pub fn read(path: &Path) -> io::Result<Reading> {
    let extension = path.extension().and_then(|value| value.to_str()).unwrap_or_default().to_ascii_lowercase();
    let mut file = File::open(path)?;
    let end = file.metadata()?.len();
    match extension.as_str() {
        "nef" => nikon(&mut Tiff::new(&mut file, 0, end)?, Source::Nef),
        "arw" => sony(&mut Tiff::new(&mut file, 0, end)?, Source::Arw),
        "pef" => pentax(&mut Tiff::new(&mut file, 0, end)?, Source::Pef),
        "dng" => pentax(&mut Tiff::new(&mut file, 0, end)?, Source::Dng),
        "raf" => fuji(&mut file, end),
        "jpg" | "jpeg" => {
            let mut tiff = Tiff::jpeg(&mut file, 0, end)?;
            let image = tiff.directory(tiff.first, None)?;
            match tiff.text(&image, 0x010F)?.unwrap_or_default().to_ascii_uppercase().as_str() {
                "NIKON" | "NIKON CORPORATION" => nikon(&mut tiff, Source::Jpeg),
                "SONY" => sony(&mut tiff, Source::Jpeg),
                _ => Err(invalid("Expected an original Nikon or Sony JPEG.")),
            }
        }
        _ => Err(invalid("Unsupported original-photo format.")),
    }
}

type Entries = Vec<[u8; 12]>;

struct Tiff<'a> {
    file: &'a mut File,
    start: u64,
    end: u64,
    little: bool,
    first: u32,
}

impl<'a> Tiff<'a> {
    fn new(file: &'a mut File, start: u64, end: u64) -> io::Result<Self> {
        let mut metadata = Self { file, start, end, little: true, first: 0 };
        let header = metadata.bytes::<8>(0)?;
        metadata.little = match &header[..2] {
            b"II" => true,
            b"MM" => false,
            _ => return Err(invalid("Invalid TIFF byte order.")),
        };
        if metadata.u16(&header[2..]) != 42 {
            return Err(invalid("Invalid TIFF header."));
        }
        metadata.first = metadata.u32(&header[4..]);
        Ok(metadata)
    }

    fn jpeg(file: &'a mut File, start: u64, end: u64) -> io::Result<Self> {
        file.seek(SeekFrom::Start(start))?;
        let mut marker = [0; 2];
        file.read_exact(&mut marker)?;
        if marker != [0xff, 0xd8] {
            return Err(invalid("Invalid JPEG header."));
        }
        while file.stream_position()?.saturating_add(4) <= end {
            file.read_exact(&mut marker)?;
            if marker[0] != 0xff {
                break;
            }
            while marker[1] == 0xff && file.stream_position()? < end {
                file.read_exact(&mut marker[1..])?;
            }
            if matches!(marker[1], 0 | 0xff | 0xd9 | 0xda) || file.stream_position()?.saturating_add(2) > end {
                break;
            }
            let exif = marker[1] == 0xe1;
            file.read_exact(&mut marker)?;
            let Some(size) = u16::from_be_bytes(marker).checked_sub(2).map(u64::from) else { break };
            let position = file.stream_position()?;
            let Some(next) = position.checked_add(size).filter(|next| *next <= end) else { break };
            if exif && size >= 14 {
                let mut signature = [0; 6];
                file.read_exact(&mut signature)?;
                if &signature == b"Exif\0\0" {
                    return Self::new(file, position + 6, next);
                }
            }
            file.seek(SeekFrom::Start(next))?;
        }
        Err(invalid("JPEG contains no readable Exif segment."))
    }

    fn read_at(&mut self, offset: u64, output: &mut [u8]) -> io::Result<()> {
        let start = self.start.checked_add(offset).filter(|start| {
            start.checked_add(output.len() as u64).is_some_and(|end| end <= self.end)
        }).ok_or_else(|| invalid("TIFF value extends beyond its metadata block."))?;
        self.file.seek(SeekFrom::Start(start))?;
        self.file.read_exact(output)
    }

    fn bytes<const LEN: usize>(&mut self, offset: impl Into<u64>) -> io::Result<[u8; LEN]> {
        let mut result = [0; LEN];
        self.read_at(offset.into(), &mut result)?;
        Ok(result)
    }

    fn u16(&self, bytes: &[u8]) -> u16 {
        let value = bytes[..2].try_into().unwrap();
        if self.little { u16::from_le_bytes(value) } else { u16::from_be_bytes(value) }
    }

    fn u32(&self, bytes: &[u8]) -> u32 {
        let value = bytes[..4].try_into().unwrap();
        if self.little { u32::from_le_bytes(value) } else { u32::from_be_bytes(value) }
    }

    fn directory(&mut self, offset: u32, available: Option<u32>) -> io::Result<Entries> {
        if offset < 8 || available.is_some_and(|size| size < 2) {
            return Err(invalid("Invalid TIFF directory offset."));
        }
        let header = self.bytes::<2>(offset)?;
        let size = u32::from(self.u16(&header)) * 12;
        if available.is_some_and(|available| size + 2 > available) {
            return Err(invalid("TIFF directory extends beyond its metadata block."));
        }
        let mut entries = vec![0; size as usize];
        self.read_at(u64::from(offset) + 2, &mut entries)?;
        Ok(entries.chunks_exact(12).map(|entry| entry.try_into().unwrap()).collect())
    }

    fn tag<'b>(&self, entries: &'b [[u8; 12]], id: u16, ty: u16) -> Option<&'b [u8; 12]> {
        entries.iter().find(|tag| self.u16(&tag[..2]) == id).filter(|tag| self.u16(&tag[2..]) == ty)
    }

    fn text(&mut self, entries: &[[u8; 12]], id: u16) -> io::Result<Option<String>> {
        let Some(tag) = self.tag(entries, id, 2) else { return Ok(None) };
        let size = self.u32(&tag[4..]) as usize;
        if !(1..=256).contains(&size) {
            return Ok(None);
        }
        let mut data = [0; 256];
        if size <= 4 {
            data[..size].copy_from_slice(&tag[8..8 + size]);
        } else {
            self.read_at(u64::from(self.u32(&tag[8..])), &mut data[..size])?;
        }
        let size = data[..size].iter().position(|byte| *byte == 0).unwrap_or(size);
        let text = data[..size].iter().map(|byte| if byte.is_ascii() { char::from(*byte) } else { '?' }).collect::<String>();
        Ok((!text.trim().is_empty()).then(|| text.trim().to_owned()))
    }

    fn number(&self, entries: &[[u8; 12]], id: u16) -> Option<u32> {
        let tag = self.tag(entries, id, 4)?;
        (self.u32(&tag[4..]) == 1).then(|| self.u32(&tag[8..]))
    }

    fn short(&self, entries: &[[u8; 12]], id: u16) -> Option<u16> {
        let tag = self.tag(entries, id, 3)?;
        (self.u32(&tag[4..]) == 1).then(|| self.u16(&tag[8..]))
    }

    fn block(&self, entries: &[[u8; 12]], id: u16, ty: u16, minimum: u32) -> io::Result<Option<(u32, u32)>> {
        let Some(tag) = self.tag(entries, id, ty) else { return Ok(None) };
        let offset = self.u32(&tag[8..]);
        let size = self.u32(&tag[4..]);
        if size < minimum || u64::from(offset) + u64::from(size) > self.end - self.start {
            return Err(invalid("Invalid TIFF metadata block."));
        }
        Ok(Some((offset, size)))
    }

    fn exif(&mut self, image: &Entries) -> io::Result<Entries> {
        self.number(image, 0x8769).map(|offset| self.directory(offset, None)).transpose().map(Option::unwrap_or_default)
    }
}

fn firmware(software: Option<String>, prefix: &str) -> Option<String> {
    let software = software?;
    software.get(..prefix.len()).filter(|start| start.eq_ignore_ascii_case(prefix))?;
    Some(software[prefix.len()..].trim().to_owned())
}

fn nikon(tiff: &mut Tiff<'_>, source: Source) -> io::Result<Reading> {
    let image = tiff.directory(tiff.first, None)?;
    let model = tiff.text(&image, 0x0110)?.as_deref().and_then(nikon_model);
    let maker = tiff.text(&image, 0x010F)?.unwrap_or_default().to_ascii_uppercase();
    let model = model.filter(|_| matches!(maker.as_str(), "NIKON" | "NIKON CORPORATION"))
        .ok_or_else(|| invalid("Expected an original Nikon photo."))?;
    let mut result = Reading::new(model, "Nikon".into(), source);
    let exif = tiff.exif(&image)?;
    result.serial = tiff.text(&exif, 0xA431)?;
    if let Some((offset, size)) = tiff.block(&exif, 0x927C, 7, 18)? {
        if &tiff.bytes::<7>(offset)? == b"Nikon\0\x02" {
            let start = tiff.start + u64::from(offset);
            let mut note = Tiff::new(tiff.file, start + 10, start + u64::from(size))?;
            let tags = note.directory(note.first, None)?;
            result.serial = note.text(&tags, 0x001D)?.or(result.serial);
            let count = |id| note.number(&tags, id).filter(|value| !matches!(*value, 0xFFFFF7FF | u32::MAX));
            let electronic = matches!(result.model.as_str(), "Nikon Z 8" | "Nikon Z 9" | "Nikon ZR");
            result.count = (!electronic).then(|| count(0x0037)).flatten()
                .map(|value| ShutterCount::photo(value, CountKind::Mechanical))
                .or_else(|| count(0x00A7).map(|value| ShutterCount::photo(value, CountKind::Unspecified)));
        }
    }
    result.firmware = firmware(tiff.text(&image, 0x0131)?, "Ver.");
    Ok(result)
}

fn fuji(file: &mut File, end: u64) -> io::Result<Reading> {
    let mut header = [0; 92];
    file.read_exact(&mut header)?;
    if &header[..16] != b"FUJIFILMCCD-RAW " {
        return Err(invalid("Expected an original Fujifilm RAF."));
    }
    let start = u64::from(u32::from_be_bytes(header[84..88].try_into().unwrap()));
    let size = u64::from(u32::from_be_bytes(header[88..].try_into().unwrap()));
    if start < header.len() as u64 || size < 2 || start + size > end {
        return Err(invalid("Invalid RAF preview bounds."));
    }
    let mut tiff = Tiff::jpeg(file, start, start + size)?;
    let image = tiff.directory(tiff.first, None)?;
    let valid_maker = tiff.text(&image, 0x010F)?.is_some_and(|maker| maker.eq_ignore_ascii_case("FUJIFILM"));
    let model = tiff.text(&image, 0x0110)?.filter(|_| valid_maker)
        .ok_or_else(|| invalid("Expected Fujifilm camera metadata."))?;
    let mut result = Reading::new(format!("Fujifilm {model}"), "Fujifilm".into(), Source::Raf);
    let exif = tiff.exif(&image)?;
    result.serial = tiff.text(&exif, 0xA431)?;
    if let Some((offset, size)) = tiff.block(&exif, 0x927C, 7, 14)? {
        let signature = tiff.bytes::<12>(offset)?;
        if &signature[..8] == b"FUJIFILM" {
            let start = tiff.start + u64::from(offset);
            let mut note = Tiff { file: tiff.file, start, end: start + u64::from(size), little: true, first: 0 };
            let tags = note.directory(u32::from_le_bytes(signature[8..].try_into().unwrap()), None)?;
            result.count = note.short(&tags, 0x1438)
                .map(|value| ShutterCount::photo(u32::from(value & 0x7FFF), CountKind::Image));
        }
    }
    result.firmware = firmware(tiff.text(&image, 0x0131)?, &format!("Digital Camera {model} Ver"));
    Ok(result)
}

fn sony(tiff: &mut Tiff<'_>, source: Source) -> io::Result<Reading> {
    let image = tiff.directory(tiff.first, None)?;
    let valid_maker = tiff.text(&image, 0x010F)?.is_some_and(|maker| maker.eq_ignore_ascii_case("SONY"));
    let model = tiff.text(&image, 0x0110)?.filter(|_| valid_maker)
        .ok_or_else(|| invalid("Expected an original Sony photo."))?;
    let mut result = Reading::new(format!("Sony {model}"), "Sony".into(), source);
    let exif = tiff.exif(&image)?;
    result.serial = tiff.text(&exif, 0xA431)?;
    if let Some((offset, size)) = tiff.block(&exif, 0x927C, 7, 6)? {
        let header = if source == Source::Jpeg && size >= 12
            && &tiff.bytes::<12>(offset)? == b"SONY DSC \0\0\0" { 12 } else { 0 };
        let note_offset = offset.checked_add(header).ok_or_else(|| invalid("Invalid Sony MakerNote offset."))?;
        let tags = tiff.directory(note_offset, Some(size - header))?;
        result.count = sony_count(tiff, &tags, &model.to_ascii_uppercase())?;
    }
    result.firmware = firmware(tiff.text(&image, 0x0131)?, &format!("{model} v"));
    Ok(result)
}

fn sony_count(tiff: &mut Tiff<'_>, tags: &Entries, model: &str) -> io::Result<Option<ShutterCount>> {
    let (index, kind) = match model {
        "ILCE-6700" | "ILCE-7CM2" | "ILCE-7CR" | "ILCE-1M2" | "ILCE-7M5" | "ILCE-7RM6" | "ILME-FX2"
            => (10, CountKind::Mechanical),
        "ILCE-6100" | "ILCE-6100A" | "ILCE-6300" | "ILCE-6400" | "ILCE-6400A" | "ILCE-6500" | "ILCE-6600"
        | "ILCE-7C" | "ILCE-7M3" | "ILCE-7RM2" | "ILCE-7RM3" | "ILCE-7RM3A" | "ILCE-7RM4" | "ILCE-7RM4A"
        | "ILCE-7SM2" | "ILCE-9" | "ILCE-9M2" | "ILCA-99M2" | "ZV-E10" | "ILCE-1" | "ILCE-7M4" | "ILCE-7RM5"
        | "ILCE-7SM3" | "ILME-FX3" => (0x3A, CountKind::Unspecified),
        _ => return Ok(None),
    };
    let Some((offset, _)) = tiff.block(tags, 0x9050, 7, 5)?.filter(|(_, size)| *size >= index + 4)
        else { return Ok(None) };
    if matches!(model, "ILCE-1M2" | "ILCE-7M5" | "ILCE-7RM6") && tiff.bytes::<5>(offset)? != [0; 5] {
        return Ok(None);
    }
    let data = tiff.bytes::<3>(u64::from(offset) + u64::from(index))?;
    let value = u32::from_le_bytes([decipher(data[0]), decipher(data[1]), decipher(data[2]), 0]);
    Ok(Some(ShutterCount::photo(value, kind)))
}

const fn decipher(value: u8) -> u8 {
    let mut candidate = 2usize;
    while candidate < 248 {
        if candidate * candidate * candidate % 249 == value as usize {
            return candidate as u8;
        }
        candidate += 1;
    }
    value
}

fn pentax(tiff: &mut Tiff<'_>, source: Source) -> io::Result<Reading> {
    let image = tiff.directory(tiff.first, None)?;
    let maker = tiff.text(&image, 0x010F)?.unwrap_or_default().to_ascii_uppercase();
    let model = tiff.text(&image, 0x0110)?.filter(|model| maker.starts_with("PENTAX")
        || (maker.starts_with("RICOH") && model.to_ascii_uppercase().starts_with("PENTAX ")))
        .ok_or_else(|| invalid("Expected an original Pentax PEF or DNG."))?;
    let name = if model.to_ascii_uppercase().starts_with("PENTAX ") { &model[7..] } else { &model };
    let mut result = Reading::new(format!("Pentax {name}"), "Pentax".into(), source);
    let exif = tiff.exif(&image)?;
    result.serial = tiff.text(&exif, 0xA431)?;
    let mut block = tiff.block(&exif, 0x927C, 7, 12)?;
    if source == Source::Dng && block.is_none() {
        block = tiff.block(&image, 0xC634, 1, 12)?;
    }
    if let Some((offset, size)) = block {
        let header = tiff.bytes::<10>(offset)?;
        if &header[..8] == b"PENTAX \0" && matches!(&header[8..], b"II" | b"MM") {
            let start = u64::from(offset);
            let mut note = Tiff { file: tiff.file, start, end: start + u64::from(size), little: header[8] == b'I', first: 0 };
            let tags = note.directory(10, None)?;
            result.serial = note.text(&tags, 0x0229)?.or(result.serial);
            let inline = |id, length| note.tag(&tags, id, 7).filter(|tag| note.u32(&tag[4..]) == length)
                .map(|tag| u32::from_be_bytes(tag[8..].try_into().unwrap()));
            if let (Some(date), Some(time), Some(encoded)) = (inline(0x0006, 4), inline(0x0007, 3), inline(0x005D, 4)) {
                result.count = Some(ShutterCount::photo(encoded ^ date ^ !(time & 0xFFFFFF00), CountKind::Unspecified));
            }
        }
    }
    result.firmware = firmware(tiff.text(&image, 0x0131)?, &format!("{model} Ver."));
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    #[test]
    fn rejects_tiff_offsets_that_escape_the_metadata_block() {
        let mut file = tempfile::tempfile().unwrap();
        file.write_all(b"II\x2A\0\xFF\xFF\xFF\xFF").unwrap();
        let mut tiff = Tiff::new(&mut file, 0, 8).unwrap();
        assert!(tiff.directory(tiff.first, None).is_err());
    }

    #[test]
    fn retains_the_sony_decipher_table() {
        for value in 2usize..248 {
            assert_eq!(decipher((value * value * value % 249) as u8), value as u8);
        }
    }
}
