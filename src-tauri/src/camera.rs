
use regex::RegexSet;
use serde::{Deserialize, Serialize};
use std::io;
use std::sync::LazyLock;

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct Device {
    pub id: String,
    pub name: Option<String>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
pub enum CountKind {
    Mechanical,
    Unspecified,
    Image,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ShutterCount {
    pub value: u32,
    pub kind: CountKind,
    pub property: Option<u32>,
    pub is_upper_bound: bool,
}

impl ShutterCount {
    pub fn photo(value: u32, kind: CountKind) -> Self {
        Self { value, kind, property: None, is_upper_bound: false }
    }

    pub fn live(value: u32, kind: CountKind, property: u32, is_upper_bound: bool) -> Self {
        Self { value, kind, property: Some(property), is_upper_bound }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
pub enum Source {
    Usb,
    Wifi,
    Nef,
    Raf,
    Arw,
    Pef,
    Dng,
    Jpeg,
}

#[derive(Clone, Copy, Debug, Serialize)]
pub enum ErrorSource {
    CanonEos,
    NikonDisplay,
    FujifilmDevice,
    SonyTemperature,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Reading {
    pub model: String,
    pub maker: String,
    pub serial: Option<String>,
    pub firmware: Option<String>,
    pub battery: Option<u32>,
    pub count: Option<ShutterCount>,
    pub source: Source,
    pub device_error_code: Option<u32>,
    pub error_source: ErrorSource,
    pub storage_issues: Option<u8>,
}

impl Reading {
    pub fn new(model: String, maker: String, source: Source) -> Self {
        Self {
            model, maker, source,
            serial: None, firmware: None, battery: None, count: None,
            device_error_code: None, error_source: ErrorSource::CanonEos, storage_issues: None,
        }
    }
}

pub const NIKON_DSLRS: &[(u16, &str, Option<u32>)] = &[
    (0x0428, "D7000", Some(150_000)),
    (0x042A, "D800", Some(200_000)),
    (0x0430, "D7100", Some(150_000)),
    (0x0434, "D610", Some(150_000)),
    (0x0436, "D810", Some(200_000)),
    (0x0437, "D750", Some(150_000)),
    (0x0439, "D7200", Some(150_000)),
    (0x043A, "D5", Some(400_000)),
    (0x043C, "D500", Some(200_000)),
    (0x043F, "D5600", Some(100_000)),
    (0x0440, "D7500", Some(150_000)),
    (0x0441, "D850", Some(200_000)),
    (0x0445, "D3500", None),
    (0x0446, "D780", Some(150_000)),
    (0x0447, "D6", Some(400_000)),
];

pub fn usb_ids(id: &str) -> Option<(u16, u16)> {
    static ID: LazyLock<regex::Regex> = LazyLock::new(|| {
        regex::Regex::new(r"(?i)vid_([0-9a-f]{4})&pid_([0-9a-f]{4})(?:[^0-9a-f]|$)").unwrap()
    });
    let caps = ID.captures(id)?;
    Some((u16::from_str_radix(&caps[1], 16).ok()?, u16::from_str_radix(&caps[2], 16).ok()?))
}

pub fn nikon_model(name: &str) -> Option<String> {
    let name = name.trim().to_ascii_uppercase();
    let model = name.strip_prefix("NIKON ").unwrap_or(&name).trim_start();
    let dslr = model.strip_prefix("DSC ").unwrap_or(model).trim_start();
    if let Some((_, model, _)) = NIKON_DSLRS.iter().find(|(_, model, _)| *model == dslr) {
        return Some(format!("Nikon {model}"));
    }

    static Z: LazyLock<regex::Regex> = LazyLock::new(|| {
        regex::Regex::new(r"^Z\s*(?:([1-9][0-9]*)(?:\s*(_2C?|_3|IIC|III|II))?|(FC?|R))$").unwrap()
    });
    let caps = Z.captures(model)?;
    match caps.get(3).map(|body| body.as_str()) {
        Some("R") => Some("Nikon ZR".into()),
        Some(body) => Some(format!("Nikon Z {}", body.to_ascii_lowercase())),
        None => {
            let generation = caps.get(2).map_or("", |item| item.as_str());
            let generation = generation.replace("_2", "II").replace("_3", "III");
            Some(format!("Nikon Z {}{generation}", &caps[1]))
        }
    }
}

pub fn nikon_usb_model(ids: Option<(u16, u16)>) -> Option<String> {
    let (0x04b0, product) = ids? else { return None };
    let (_, model, _) = NIKON_DSLRS.iter().find(|(id, _, _)| *id == product)?;
    Some(format!("Nikon {model}"))
}

const MAKERS: [&str; 10] = [
    "Nikon", "Fujifilm", "Sony", "Panasonic", "OM System", "Olympus", "Pentax", "Ricoh", "Leica", "Hasselblad",
];
static FAMILIES: LazyLock<RegexSet> = LazyLock::new(|| {
    RegexSet::new([
        r"(?i)^(?:NIKON (?:DSC )?)?(?:D\d|Z\s*(?:\d|F[C]?\b|R\b)|COOLPIX\b)",
        r"(?i)^(?:FUJIFILM )?(?:X-[A-Z]+\d|X\d|X HALF\b|GFX ?\d|GFX ETERNA |FinePix )",
        r"(?i)^(?:SONY )?(?:ILC[AE]-|ILME-|ILX-|DSC-|NEX-|SLT-|ZV-|ALPHA |α)",
        r"(?i)^(?:PANASONIC )?(?:LUMIX\b|(?:DC|DMC)-[A-Z]+\d)",
        r"(?i)^(?:OM SYSTEM )?OM-[135](?:\b|MARK)",
        r"(?i)^(?:OLYMPUS |OM-D )?(?:E-M\d|E-P(?:L|M)?\d|PEN-F\b|TG-\d)",
        r"(?i)^(?:PENTAX )?(?:K-?\d|K-?[FPSX]\b|Optio )",
        r"(?i)^(?:RICOH GR\b|(?:RICOH )?(?:GR ?(?:DIGITAL|I|II|III|IV|IIIx)\b|WG-\d))",
        r"(?i)^(?:LEICA )?(?:SL\d?|Q\d?|M\d+|M EV1|[DV]-LUX)(?:\b|$)",
        r"(?i)^(?:HASSELBLAD )?(?:X[12]D\b|907X\b|CFV\b|H[3456]D\b)",
    ]).unwrap()
});

pub fn usb_maker(ids: Option<(u16, u16)>, name: &str) -> Option<&'static str> {
    let uppercase = name.to_ascii_uppercase();
    if matches!(ids, Some((0x04a9, _)))
        || (ids.is_none() && uppercase.starts_with("CANON ") &&
            (uppercase.contains("EOS") || uppercase.contains("POWERSHOT")))
    {
        return Some("Canon");
    }
    if let Some(index) = FAMILIES.matches(name).iter().next() {
        return Some(MAKERS[index]);
    }
    match ids {
        Some((0x04cb, 0x0313)) => Some("Fujifilm"),
        Some((0x04b0, 0x0442 | 0x0443 | 0x0444 | 0x0448 | 0x044b | 0x044c |
                      0x044f | 0x0450 | 0x0451 | 0x0452 | 0x0453 | 0x0454 | 0x0455)) => Some("Nikon"),
        Some((0x054c, 0x0f50 | 0x0f56)) => Some("Sony"),
        Some((0x25fb, 0x2123)) => Some("Ricoh"),
        Some((0x25fb, 0x018f)) => Some("Pentax"),
        _ => None,
    }
}

pub fn invalid(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}


#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn classifies_cameras_without_requiring_an_explicit_usb_product_id() {
        for (model, maker) in [
            ("Nikon Z 6II", "Nikon"), ("Fujifilm X-E5", "Fujifilm"), ("Sony ILCE-7M5", "Sony"),
            ("Panasonic DC-S5M2", "Panasonic"), ("OM-1 Mark II", "OM System"),
            ("Olympus E-M1 Mark III", "Olympus"), ("Pentax K-3 Mark III", "Pentax"),
            ("Ricoh GR III", "Ricoh"), ("Leica M11", "Leica"), ("Hasselblad X2D 100C", "Hasselblad"),
        ] {
            assert_eq!(usb_maker(None, model), Some(maker), "{model}");
        }
        assert_eq!(usb_maker(None, "Portable music player"), None);
    }
}
