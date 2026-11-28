
use crate::camera::{ErrorSource, Reading};
use crate::ptp::{le16, le32, le64, Direction, Transport, GET_PROPERTY};
use std::io;

pub const FULL: u8 = 1;
pub const READ_ONLY: u8 = 2;
pub const UNAVAILABLE: u8 = 4;
pub const BUSY: u8 = 8;
pub const UNFORMATTED: u8 = 16;
pub const ACCESS_ERROR: u8 = 32;

pub fn read_status(
    transport: &mut impl Transport, reading: &mut Reading, vendor: u16,
) -> io::Result<()> {
    let property = match vendor {
        0x04b0 => Some((GET_PROPERTY, 0xD1B2, ErrorSource::NikonDisplay)),
        0x04cb => Some((GET_PROPERTY, 0xD21B, ErrorSource::FujifilmDevice)),
        0x054c if reading.maker.to_ascii_uppercase().starts_with("SONY") =>
            Some((0x9204, 0xD251, ErrorSource::SonyTemperature)),
        _ => None,
    };
    if let Some((operation, property, source)) = property {
        if let Some(data) = transport.read(operation, &[property])? {
            let code = match (source, data.as_slice()) {
                (ErrorSource::FujifilmDevice, [_, _]) => Some(u32::from(le16(&data))),
                (ErrorSource::NikonDisplay | ErrorSource::SonyTemperature, [value]) => Some(u32::from(*value)),
                _ => None,
            };
            if let Some(code) = code {
                reading.device_error_code = Some(code);
                reading.error_source = source;
            }
        }
    }
    Ok(())
}

pub fn read_storage(transport: &mut impl Transport, vendor: u16) -> io::Result<Option<u8>> {
    let reply = transport.execute(0x1004, &[], Direction::In)?;
    let Some(data) = reply.data.filter(|data| data.len() >= 4) else {
        return Ok(storage_response(reply.code, vendor));
    };
    let count = le32(&data) as usize;
    if count > 16 || data.len() != 4 + count * 4 {
        return Ok(None);
    }
    let mut issues = None;
    for id in data[4..].chunks_exact(4).map(le32).filter(|id| id & 0xffff != 0 && *id != u32::MAX) {
        let reply = transport.execute(0x1005, &[id], Direction::In)?;
        let item = if let Some(data) = reply.data.filter(|data| data.len() >= 28) {
            classify_storage(
                Some(u32::from(le16(&data))),
                Some(u32::from(le16(&data[4..]))),
                Some(le64(&data[6..])),
                Some(le64(&data[14..])),
            )
        } else {
            storage_response(reply.code, vendor)
        };
        if let Some(item) = item {
            issues = Some(issues.unwrap_or_default() | item);
        }
    }
    Ok(issues)
}

pub fn classify_storage(kind: Option<u32>, access: Option<u32>, capacity: Option<u64>, free: Option<u64>) -> Option<u8> {
    if matches!(kind, Some(1 | 2 | 5..)) {
        return None;
    }
    let space = capacity.zip(free).is_some_and(|(capacity, free)| 0 < capacity && capacity < u64::MAX && free <= capacity);
    let readonly = matches!(access, Some(1 | 2));
    let result = if readonly { READ_ONLY } else { 0 } | if space && free == Some(0) { FULL } else { 0 };
    (space || access.is_some_and(|value| value <= 2)).then_some(result)
}

fn storage_response(response: u16, vendor: u16) -> Option<u8> {
    match (response, vendor) {
        (0x200C, _) => Some(FULL),
        (0x200E, _) => Some(READ_ONLY),
        (0x2013, _) => Some(UNAVAILABLE),
        (0x2019, _) => Some(BUSY),
        (0x200F, _) | (0xA021, 0x04b0) => Some(ACCESS_ERROR),
        (0xA022, 0x04b0) => Some(UNFORMATTED),
        _ => None,
    }
}
