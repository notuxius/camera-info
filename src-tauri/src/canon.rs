
use crate::camera::{CountKind, Reading, ShutterCount};
use crate::ptp::{le32, Direction, Transport, GET_PROPERTY, OK};
use std::{io, thread, time::Duration};

pub const LEGACY_PROPERTY: u32 = 0xD1AC;
pub const RELEASE_PROPERTY: u32 = 0xD167;
const MONITOR_ADDRESS: u32 = 0x1001;
const RELEASE_STEP: u32 = 1000;
const SET_REMOTE_MODE: u16 = 0x9114;
const SET_EVENT_MODE: u16 = 0x9115;
const GET_EVENT: u16 = 0x9116;
const GET_DEVICE_INFO_EX: u16 = 0x9108;
const REQUEST_PROPERTY: u16 = 0x9127;
const SEND_MONITOR: u16 = 0x9052;
const READ_MONITOR: u16 = 0x9053;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Protocol {
    Unknown,
    Legacy,
    Monitor,
    Release,
}

pub fn model_key(model: &str) -> String {
    let key: String = model.to_uppercase().chars().filter(char::is_ascii_alphanumeric).collect();
    let key = key.strip_prefix("CANON").unwrap_or(&key);
    match key {
        "EOSREBELT8I" | "EOSKISSX10I" => "EOS850D",
        "EOS6DM2" => "EOS6DMARKII",
        "EOS5RM2" | "EOSR5M2" | "EOS5RMARKII" => "EOSR5MARKII",
        "EOSR6M2" => "EOSR6MARKII",
        "EOSR6M3" => "EOSR6MARKIII",
        "EOSM50M2" | "EOSKISSM2" => "EOSM50MARKII",
        _ => key,
    }.into()
}

pub fn usb_model(ids: Option<(u16, u16)>) -> Option<String> {
    let (0x04a9, product) = ids? else { return None };
    let model = match product {
        0x3199 => "5D Mark II",
        0x319A => "7D",
        0x323A => "5D Mark III",
        0x3250 => "6D",
        0x3253 => "70D",
        0x326F => "7D Mark II",
        0x3281 => "5D Mark IV",
        0x3294 => "80D",
        0x32CA => "6D Mark II",
        0x32DA => "R",
        0x32E2 => "RP",
        0x32EA => "90D",
        0x32F1 => "850D",
        0x32F4 => "R5",
        0x32F5 => "R6",
        0x32F7 => "R7",
        0x32F8 => "R10",
        0x32F9 => "M50 Mark II",
        0x32FC => "R3",
        0x3303 => "R5 C",
        0x330B => "R6 Mark II",
        0x330C => "R8",
        0x330D => "R50",
        0x3312 => "R100",
        0x3313 => "R1",
        0x3314 => "R5 Mark II",
        0x3320 => "R50 V",
        0x3323 => "R6 Mark III",
        _ => return None,
    };
    Some(format!("Canon EOS {model}"))
}

pub fn protocol_for(model: &str) -> Protocol {
    match model_key(model).as_str() {
        "EOS5DMARKII" | "EOS5DMARKIII" | "EOS6D" | "EOS7D" | "EOS7DMARKII" | "EOS70D" => Protocol::Legacy,
        "EOS5DMARKIV" | "EOS6DMARKII" | "EOS80D" => Protocol::Monitor,
        "EOS90D" | "EOS850D" | "EOSM50MARKII" | "EOSR" | "EOSRP" | "EOSR5" | "EOSR5MARKII" | "EOSR6"
        | "EOSR6MARKII" | "EOSR6MARKIII" | "EOSR7" | "EOSR8" | "EOSR10" | "EOSR50" | "EOSR100" => Protocol::Release,
        _ => Protocol::Unknown,
    }
}

pub fn decode_release(data: &[u8]) -> Option<ShutterCount> {
    if data.len() != 16 || le32(data) != 16 || le32(&data[4..]) != 1 {
        return None;
    }
    let mechanical = le32(&data[8..]);
    let total = le32(&data[12..]);
    (mechanical <= total && mechanical % RELEASE_STEP == 0 && total % RELEASE_STEP == 0)
        .then(|| ShutterCount::live(mechanical, CountKind::Mechanical, RELEASE_PROPERTY, true))
}

pub fn decode_monitor(data: &[u8]) -> Option<ShutterCount> {
    if data.len() != 10 {
        return None;
    }
    let high = u32::from(u16::from_be_bytes([data[0], data[1]]));
    let mut low = data[2..].iter().copied().filter(|byte| *byte != 0);
    let value = (high << 8) | u32::from(low.next().unwrap_or_default());
    low.next().is_none().then(|| ShutterCount::live(value, CountKind::Mechanical, MONITOR_ADDRESS, false))
}

struct Events {
    protocol: Protocol,
    legacy: Option<u32>,
    scalar_release: Option<u32>,
    mechanical_release: Option<ShutterCount>,
    error: Option<u32>,
    battery: Option<u32>,
}

impl Events {
    fn parse(&mut self, mut data: &[u8]) {
        while data.len() >= 8 {
            let size = le32(data) as usize;
            let code = le32(&data[4..]);
            if size < 8 || size > data.len() || (size == 8 && code == 0) {
                return;
            }

            if code == 0xC189 && size >= 16 {
                let property = le32(&data[8..]);
                let payload = &data[12..size];
                if payload.len() == 4 {
                    let value = le32(payload);
                    match property {
                        0xD1C4 => self.error = Some(value),
                        0xD111 => self.battery = match value {
                            1 => Some(50), 2 => Some(100), 4 => Some(75), 5 => Some(25), _ => None,
                        },
                        LEGACY_PROPERTY if value != u32::MAX => self.legacy = Some(value),
                        RELEASE_PROPERTY if value != u32::MAX => self.scalar_release = Some(value),
                        _ => {}
                    }
                } else if property == RELEASE_PROPERTY && self.protocol == Protocol::Release {
                    self.mechanical_release = decode_release(payload).or(self.mechanical_release);
                }
            }
            data = &data[size..];
        }
    }

    fn has_mechanical(&self) -> bool {
        self.mechanical_release.is_some() || (self.protocol == Protocol::Legacy && self.legacy.is_some())
    }

    fn apply(self, reading: &mut Reading, wifi: bool) {
        reading.count = self.mechanical_release
            .or_else(|| self.legacy.map(|value| ShutterCount::live(
                value,
                if self.protocol == Protocol::Legacy { CountKind::Mechanical } else { CountKind::Unspecified },
                LEGACY_PROPERTY,
                false,
            )))
            .or_else(|| self.scalar_release.map(|value|
                ShutterCount::live(value, CountKind::Unspecified, RELEASE_PROPERTY, false)));
        reading.device_error_code = self.error;
        if wifi {
            reading.battery = reading.battery.or(self.battery);
        }
    }
}

pub fn read_eos(
    transport: &mut impl Transport,
    reading: &mut Reading,
    advertised_operations: Option<&[u16]>,
    identified_usb_model: bool,
) -> io::Result<()> {
    let wifi = advertised_operations.is_some();
    let supports = |operation| advertised_operations.is_none_or(|ops| ops.contains(&operation));
    let protocol = protocol_for(&reading.model);
    if !wifi && identified_usb_model && protocol == Protocol::Monitor {
        if let Some(count) = read_monitor(transport)? {
            reading.count = Some(count);
            return Ok(());
        }
    }
    let legacy = if supports(GET_PROPERTY) && (!wifi || protocol != Protocol::Unknown) {
        transport.read(GET_PROPERTY, &[LEGACY_PROPERTY])?
            .filter(|data| data.len() == 4).map(|data| le32(&data)).filter(|value| *value != u32::MAX)
    } else {
        None
    };
    let mut report = Events {
        protocol, legacy, scalar_release: None, mechanical_release: None, error: None, battery: None,
    };
    if (!wifi && legacy.is_some() && protocol != Protocol::Release)
        || !supports(GET_EVENT) || !supports(SET_EVENT_MODE)
    {
        report.apply(reading, wifi);
        return Ok(());
    }
    let mut remote = false;
    let mut events = false;
    let result = (|| {
        if supports(SET_REMOTE_MODE) {
            remote = transport.control(SET_REMOTE_MODE, &[1])?;
        }
        events = transport.control(SET_EVENT_MODE, &[1])?;
        if !events {
            return Ok(());
        }
        if supports(GET_DEVICE_INFO_EX) {
            transport.read(GET_DEVICE_INFO_EX, &[])?;
        }
        poll(transport, &mut report, 3, wifi)?;
        if supports(REQUEST_PROPERTY) {
            for property in if protocol == Protocol::Release {
                &[RELEASE_PROPERTY, LEGACY_PROPERTY][..]
            } else {
                &[LEGACY_PROPERTY, RELEASE_PROPERTY][..]
            } {
                if report.has_mechanical() {
                    break;
                }
                if (*property == LEGACY_PROPERTY && report.legacy.is_some())
                    || (*property == RELEASE_PROPERTY && protocol != Protocol::Release && report.scalar_release.is_some())
                {
                    continue;
                }
                if transport.control(REQUEST_PROPERTY, &[*property])? {
                    poll(transport, &mut report, 4, wifi)?;
                }
            }
        }
        Ok(())
    })();
    let disconnected = result.as_ref().is_err_and(|error: &io::Error| error.kind() == io::ErrorKind::NotConnected);
    if !disconnected {
        if events {
            transport.cleanup(SET_EVENT_MODE, &[0]);
        }
        if remote {
            transport.cleanup(SET_REMOTE_MODE, &[0]);
        }
    }
    report.apply(reading, wifi);
    result
}

fn poll(transport: &mut impl Transport, events: &mut Events, attempts: usize, wifi: bool) -> io::Result<()> {
    for attempt in 0..attempts {
        if attempt > 0 {
            thread::sleep(Duration::from_millis(160));
        }
        let Some(data) = transport.read(GET_EVENT, &[])? else { break };
        events.parse(&data);
        if events.has_mechanical() && (!wifi || events.battery.is_some()) {
            break;
        }
    }
    Ok(())
}

fn read_monitor(transport: &mut impl Transport) -> io::Result<Option<ShutterCount>> {
    let session_payload = |name: &[u8]| {
        let mut result = vec![0; name.len() + 4 + 24];
        result[..name.len()].copy_from_slice(name);
        result
    };
    let open = session_payload(b"MonOpen\0");
    if transport.execute(SEND_MONITOR, &[0, 0], Direction::Out(&open))?.code != OK {
        return Ok(None);
    }
    let result = (|| {
        let name = b"MonReadAndGetData\0";
        let mut command = vec![0; name.len() + 4 + 3 * 20 + 4];
        command[..name.len()].copy_from_slice(name);
        command[name.len()..name.len() + 4].copy_from_slice(&3u32.to_le_bytes());
        for (record, argument) in command[name.len() + 4..].chunks_exact_mut(20).zip([2u32, MONITOR_ADDRESS, 10]) {
            record[..4].copy_from_slice(&2u32.to_le_bytes());
            record[4..8].copy_from_slice(&argument.to_le_bytes());
        }
        if transport.execute(SEND_MONITOR, &[0, 1], Direction::Out(&command))?.code != OK {
            return Ok(None);
        }
        Ok(transport.read(READ_MONITOR, &[0, 0, 1])?.as_deref().and_then(decode_monitor))
    })();
    let disconnected = result.as_ref().is_err_and(|error: &io::Error| error.kind() == io::ErrorKind::NotConnected);
    if !disconnected {
        let close = session_payload(b"MonClose\0");
        let _ = transport.execute(SEND_MONITOR, &[0, 0], Direction::Out(&close));
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_known_monitor_capture_and_rejects_unknown_layout() {
        assert_eq!(decode_monitor(&[2, 226, 124, 0, 0, 0, 0, 0, 0, 0]).unwrap().value, 189_052);
        assert!(decode_monitor(&[2, 226, 124, 1, 0, 0, 0, 0, 0, 0]).is_none());
    }

    #[test]
    fn distinguishes_an_upper_bound_from_scalar_counts() {
        let data = [16u32, 1, 87_000, 94_000].into_iter().flat_map(u32::to_le_bytes).collect::<Vec<_>>();
        let count = decode_release(&data).unwrap();
        assert_eq!(count.value, 87_000);
        assert!(count.is_upper_bound);
        assert!(decode_release(&data[..15]).is_none());
        assert_eq!(protocol_for("Canon EOS Rebel T8i"), Protocol::Release);
    }
}
