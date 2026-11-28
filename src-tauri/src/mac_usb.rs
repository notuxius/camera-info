
use camera_inspector::camera::{invalid, nikon_model, nikon_usb_model, usb_maker, Device, Reading, Source};
use camera_inspector::canon;
use camera_inspector::ptp::{DeviceInfo, Direction, Reply, Transport, GET_DEVICE_INFO, GET_PROPERTY, OK};
use camera_inspector::usb;
use serde::Deserialize;
use std::{ffi::{c_char, CString}, io, ptr, slice};

#[repr(C)]
struct CaptureResult {
    bytes: *mut u8,
    length: usize,
    status: i32,
}

unsafe extern "C" {
    fn ci_mac_scan() -> CaptureResult;
    fn ci_mac_open(identifier: *const c_char) -> CaptureResult;
    fn ci_mac_command(header: *const u8, header_length: usize, outgoing: *const u8, outgoing_length: usize) -> CaptureResult;
    fn ci_mac_close();
    fn ci_mac_free(data: *mut u8);
}

impl CaptureResult {
    fn data(&self) -> &[u8] {
        if self.bytes.is_null() || self.length == 0 { return &[]; }
        // The bridge owns these bytes until the result is dropped.
        unsafe { slice::from_raw_parts(self.bytes, self.length) }
    }

    fn error(&self) -> io::Error {
        let (kind, message) = match self.status {
            -2 => (io::ErrorKind::NotConnected, "The camera disconnected."),
            -3 => (io::ErrorKind::TimedOut, "The camera did not respond."),
            _ => (io::ErrorKind::Other, "Image Capture could not open the camera."),
        };
        let reason = String::from_utf8_lossy(self.data());
        io::Error::new(kind, if reason.is_empty() { message.into() } else { reason.into_owned() })
    }
}

impl Drop for CaptureResult {
    fn drop(&mut self) {
        unsafe { ci_mac_free(self.bytes) }
    }
}

#[derive(Deserialize)]
struct Camera {
    id: String,
    name: String,
    vendor: u16,
    product: u16,
    serial: String,
    battery: Option<u32>,
    ptp: bool,
}

pub fn find() -> io::Result<Vec<Device>> {
    let result = unsafe { ci_mac_scan() };
    if result.status < 0 { return Err(result.error()); }
    let cameras: Vec<Camera> = serde_json::from_slice(result.data())?;
    Ok(cameras.into_iter().map(|camera| {
        let name = nikon_usb_model(Some((camera.vendor, camera.product)))
            .or_else(|| (!camera.name.is_empty()).then_some(camera.name));
        Device { id: camera.id, name }
    }).collect())
}

pub fn read(device: Device) -> io::Result<Reading> {
    let id = CString::new(device.id).map_err(|_| invalid("Invalid camera identifier."))?;
    let descriptor = unsafe { ci_mac_open(id.as_ptr()) };
    if descriptor.status < 0 { return Err(descriptor.error()); }
    let mut link = Session { transaction: 0 };
    let camera: Camera = serde_json::from_slice(descriptor.data())?;
    let ids = Some((camera.vendor, camera.product));
    let canon_usb_model = canon::usb_model(ids);
    let nikon_usb = nikon_usb_model(ids);
    let maker = usb_maker(ids, &camera.name).unwrap_or("—");
    let model = (!camera.name.is_empty()).then_some(camera.name)
        .unwrap_or_else(|| "Unknown camera".into());
    let mut reading = Reading::new(model, maker.into(), Source::Usb);
    reading.serial = (!camera.serial.is_empty()).then_some(camera.serial);
    reading.battery = camera.battery;
    if !camera.ptp {
        reading.model = canon_usb_model.or(nikon_usb).unwrap_or(reading.model);
        return Ok(reading);
    }

    let mut advertised_battery = false;
    if let Some(info) = link.read(GET_DEVICE_INFO, &[])?.and_then(|data| DeviceInfo::parse(&data, Source::Usb).ok()) {
        reading.firmware = info.reading.firmware;
        reading.serial = info.reading.serial.or(reading.serial);
        if info.reading.maker != "—" { reading.maker = info.reading.maker; }
        if info.reading.model != "Unknown camera" { reading.model = info.reading.model; }
        advertised_battery = info.properties.contains(&0x5001);
    }
    if let Some(model) = nikon_usb.as_ref() {
        reading.model.clone_from(model);
    } else if let Some(model) = canon_usb_model.as_ref() {
        if canon::model_key(model) != canon::model_key(&reading.model) {
            reading.model.clone_from(model);
        }
    } else if reading.maker.to_ascii_uppercase().starts_with("NIKON") {
        reading.model = nikon_model(&reading.model).unwrap_or(reading.model);
    }
    if reading.battery.is_none() && advertised_battery {
        if let Some(bytes) = link.read(GET_PROPERTY, &[0x5001])? {
            if let [value @ 0..=100] = bytes.as_slice() {
                reading.battery = Some(u32::from(*value));
            }
        }
    }
    if camera.vendor == 0x04a9 && canon::model_key(&reading.model).starts_with("EOS") {
        canon::read_eos(&mut link, &mut reading, None, canon_usb_model.is_some())?;
    } else {
        usb::read_status(&mut link, &mut reading, camera.vendor)?;
    }
    reading.storage_issues = usb::read_storage(&mut link, camera.vendor)?;
    Ok(reading)
}

struct Session { transaction: u32 }

impl Transport for Session {
    fn execute(&mut self, command: u16, args: &[u32], direction: Direction<'_>) -> io::Result<Reply> {
        self.transaction = self.transaction.wrapping_add(1);
        let mut packet = Vec::from((12 + args.len() as u32 * 4).to_le_bytes());
        packet.extend(1u16.to_le_bytes());
        packet.extend(command.to_le_bytes());
        packet.extend(self.transaction.to_le_bytes());
        packet.extend(args.iter().copied().flat_map(u32::to_le_bytes));
        let (data, length) = match direction {
            Direction::Out(data) => (data.as_ptr(), data.len()),
            _ => (ptr::null(), 0),
        };
        let result = unsafe { ci_mac_command(packet.as_ptr(), packet.len(), data, length) };
        let code = match result.status {
            -4 => 0x2005,
            status if status < 0 => return Err(result.error()),
            status => status as u16,
        };
        // ImageCaptureCore supplies the inbound data phase separately from the PTP response.
        let data = (code == OK && matches!(direction, Direction::In)).then(|| result.data().to_vec());
        Ok(Reply { code, data })
    }
}

impl Drop for Session {
    fn drop(&mut self) {
        unsafe { ci_mac_close() }
    }
}
