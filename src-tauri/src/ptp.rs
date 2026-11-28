
use crate::camera::{invalid, Reading, Source};
use std::io;

pub const OK: u16 = 0x2001;
pub const MAX_DATA: usize = 1024 * 1024;
pub const GET_DEVICE_INFO: u16 = 0x1001;
pub const GET_PROPERTY: u16 = 0x1015;

pub enum Direction<'a> {
    None,
    In,
    Out(&'a [u8]),
}

pub struct Reply {
    pub code: u16,
    pub data: Option<Vec<u8>>,
}

pub trait Transport {
    fn execute(&mut self, command: u16, args: &[u32], mode: Direction<'_>) -> io::Result<Reply>;

    fn read(&mut self, command: u16, args: &[u32]) -> io::Result<Option<Vec<u8>>> {
        let reply = self.execute(command, args, Direction::In)?;
        Ok((reply.code == OK).then_some(reply.data).flatten())
    }

    fn control(&mut self, command: u16, args: &[u32]) -> io::Result<bool> {
        Ok(self.execute(command, args, Direction::None)?.code == OK)
    }
}

pub fn le16(bytes: &[u8]) -> u16 {
    u16::from_le_bytes(bytes[..2].try_into().unwrap())
}

pub fn le32(bytes: &[u8]) -> u32 {
    u32::from_le_bytes(bytes[..4].try_into().unwrap())
}

pub fn le64(bytes: &[u8]) -> u64 {
    u64::from_le_bytes(bytes[..8].try_into().unwrap())
}

pub struct DeviceInfo {
    pub reading: Reading,
    pub operations: Vec<u16>,
    pub properties: Vec<u16>,
}

fn take<'a>(data: &mut &'a [u8], length: usize) -> io::Result<&'a [u8]> {
    let (value, remainder) = data.split_at_checked(length)
        .ok_or_else(|| invalid("Truncated PTP device information."))?;
    *data = remainder;
    Ok(value)
}

fn text(data: &mut &[u8]) -> io::Result<Option<String>> {
    let size = usize::from(take(data, 1)?[0]);
    if size == 0 {
        return Ok(None);
    }
    let value: Vec<u16> = take(data, size * 2)?.chunks_exact(2).map(le16).collect();
    if value.last() != Some(&0) {
        return Err(invalid("Invalid PTP string."));
    }
    let value = String::from_utf16_lossy(&value[..size - 1]);
    let value = value.trim();
    Ok((!value.is_empty()).then(|| value.to_owned()))
}

fn codes<'a>(data: &mut &'a [u8]) -> io::Result<impl Iterator<Item = u16> + 'a> {
    let count = le32(take(data, 4)?) as usize;
    let bytes = count.checked_mul(2).ok_or_else(|| invalid("Invalid PTP code list."))?;
    Ok(take(data, bytes)?.chunks_exact(2).map(le16))
}

impl DeviceInfo {
    pub fn parse(mut data: &[u8], source: Source) -> io::Result<Self> {
        take(&mut data, 8)?;
        text(&mut data)?;
        take(&mut data, 2)?;
        let operations = codes(&mut data)?.collect();
        codes(&mut data)?;
        let properties = codes(&mut data)?.collect();
        codes(&mut data)?;
        codes(&mut data)?;
        let maker = text(&mut data)?.unwrap_or_else(|| "—".into());
        let mut model = text(&mut data)?.unwrap_or_else(|| "Unknown camera".into());
        if maker.to_ascii_uppercase().starts_with("CANON") && model.to_ascii_uppercase().starts_with("EOS ") {
            model = format!("Canon {model}");
        }
        let mut reading = Reading::new(model, maker, source);
        reading.firmware = text(&mut data)?;
        reading.serial = text(&mut data)?;
        Ok(Self { reading, operations, properties })
    }
}
