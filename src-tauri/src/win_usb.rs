
use camera_inspector::camera::{nikon_model, nikon_usb_model, usb_ids, usb_maker, Device, Reading, Source};
use camera_inspector::canon;
use camera_inspector::ptp::{Direction, Reply, Transport, MAX_DATA, OK};
use camera_inspector::usb;
use std::{io, mem::{size_of, ManuallyDrop}, ptr, slice};
use windows::{
    core::{GUID, HSTRING, Interface, PCWSTR, PWSTR, Result, w},
    Win32::{
        Devices::PortableDevices::*,
        Foundation::PROPERTYKEY,
        System::{
            Com::{
                CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize,
                StructuredStorage::{PROPVARIANT, PROPVARIANT_0, PROPVARIANT_0_0, PROPVARIANT_0_0_0},
            },
            Variant::VT_UI4,
        },
    },
};

const CLIENT: GUID = GUID::from_u128(0x204d9f0c_2292_4080_9f42_40664e70f859);
const DEVICE: GUID = GUID::from_u128(0x26d4979a_e643_4626_9e2b_736dc0c92fdc);
const COMMON: GUID = GUID::from_u128(0xf0422a9c_5dc8_4440_b5bd_5df28835658a);
const MTP: GUID = GUID::from_u128(0x4d545058_1a2e_4106_a357_771e0819fc56);
const STORAGE: GUID = GUID::from_u128(0x01a3057a_74d6_4e80_bea7_dc4c212ce50a);

const fn key(category: GUID, id: u32) -> PROPERTYKEY {
    PROPERTYKEY { fmtid: category, pid: id }
}

fn io_error(error: windows::core::Error) -> io::Error {
    let kind = match error.code().0 as u32 {
        0x8007048F | 0x800701B1 => io::ErrorKind::NotConnected,
        0x80070005 => io::ErrorKind::PermissionDenied,
        _ => io::ErrorKind::Other,
    };
    io::Error::new(kind, error)
}

struct Com;

impl Com {
    fn open() -> Result<Self> {
        unsafe { CoInitializeEx(None, COINIT_MULTITHREADED).ok()? };
        Ok(Self)
    }
}

impl Drop for Com {
    fn drop(&mut self) {
        unsafe { CoUninitialize() };
    }
}

fn create<T: Interface>(class: &GUID) -> Result<T> {
    unsafe { CoCreateInstance(class, None, CLSCTX_INPROC_SERVER) }
}

fn values() -> Result<IPortableDeviceValues> {
    create(&PortableDeviceValues)
}

fn take_string(value: PWSTR) -> Option<String> {
    if value.is_null() {
        return None;
    }
    let text = unsafe { value.to_string().ok() };
    unsafe { CoTaskMemFree(Some(value.0.cast())) };
    text.map(|value| value.trim().to_owned()).filter(|value| !value.is_empty())
}

pub fn find() -> io::Result<Vec<Device>> {
    (|| -> Result<_> {
        let _com = Com::open()?;
        let manager: IPortableDeviceManager = create(&PortableDeviceManager)?;
        let mut count = 0;
        unsafe { manager.RefreshDeviceList()?; manager.GetDevices(ptr::null_mut(), &mut count)?; }
        if count == 0 || count > 4096 {
            return Ok(vec![]);
        }
        let mut identifiers = vec![PWSTR::null(); count as usize];
        let result = unsafe { manager.GetDevices(identifiers.as_mut_ptr(), &mut count) };
        let identifiers: Vec<_> = identifiers.into_iter().filter_map(take_string).collect();
        result?;
        Ok(identifiers.into_iter().filter_map(|id| {
            let ids = usb_ids(&id);
            let id_wide = HSTRING::from(&id);
            let name = nikon_usb_model(ids).or_else(|| friendly_name(&manager, PCWSTR(id_wide.as_ptr())));
            if nikon_usb_model(ids).is_some() || usb_maker(ids, name.as_deref().unwrap_or_default()).is_some()
                || still_camera(&manager, PCWSTR(id_wide.as_ptr()))
            {
                Some(Device { id, name })
            } else {
                None
            }
        }).collect())
    })().map_err(io_error)
}

fn friendly_name(manager: &IPortableDeviceManager, id: PCWSTR) -> Option<String> {
    let mut length = 0;
    let _ = unsafe { manager.GetDeviceFriendlyName(id, PWSTR::null(), &mut length) };
    if !(1..=4096).contains(&length) { return None; }
    let mut text = vec![0u16; length as usize];
    unsafe { manager.GetDeviceFriendlyName(id, PWSTR(text.as_mut_ptr()), &mut length).ok()? };
    let end = text.iter().position(|unit| *unit == 0).unwrap_or(text.len());
    let cleaned = String::from_utf16_lossy(&text[..end]).trim().to_owned();
    (!cleaned.is_empty()).then_some(cleaned)
}

fn still_camera(manager: &IPortableDeviceManager, id: PCWSTR) -> bool {
    let mut value = 0u32;
    let mut size = size_of::<u32>() as u32;
    let mut kind = 0;
    (unsafe {
        manager.GetDeviceProperty(id, w!("PORTABLE_DEVICE_TYPE"), ptr::from_mut(&mut value).cast(), &mut size, &mut kind)
            .is_ok()
    }) && size == 4 && kind == 4 && value == 1
}

pub fn read(camera: Device) -> io::Result<Reading> {
    let _com = Com::open().map_err(io_error)?;
    let client = values().map_err(io_error)?;
    let device: IPortableDevice = create(&PortableDeviceFTM).map_err(io_error)?;
    let id = HSTRING::from(&camera.id);
    let allow_commands = unsafe {
        client.SetStringValue(&key(CLIENT, 2), w!("Camera Inspector")).map_err(io_error)?;
        match device.Open(&id, &client) {
            Ok(()) => true,
            Err(error) if error.code().0 as u32 == 0x80070005 => {
                client.SetUnsignedIntegerValue(&key(CLIENT, 9), 0x80000000).map_err(io_error)?;
                device.Open(&id, &client).map_err(io_error)?;
                false
            }
            Err(error) => return Err(io_error(error)),
        }
    };
    let mut session = Session(device);
    let ids = usb_ids(&camera.id);
    let vendor = ids.map_or(0, |(vendor, _)| vendor);
    let nikon = nikon_usb_model(ids).or_else(|| camera.name.as_deref().filter(|name|
        name.to_ascii_uppercase().starts_with("NIKON ")).and_then(nikon_model));
    let name = nikon.clone().or(camera.name).unwrap_or_else(|| "Unknown camera".into());
    let maker = usb_maker(ids, &name).unwrap_or("—");
    let mut reading = Reading::new(name, maker.into(), Source::Usb);
    if let Err(error) = details(&session.0, &mut reading) {
        let error = io_error(error);
        if error.kind() == io::ErrorKind::NotConnected {
            return Err(error);
        }
    }
    let canon_model = canon::usb_model(ids);
    if let Some(model) = nikon.or_else(|| canon_model.clone()) {
        reading.model = model;
    } else if maker == "Nikon" {
        reading.model = nikon_model(&reading.model).unwrap_or(reading.model);
    }
    if allow_commands {
        if maker == "Canon" && canon::model_key(&reading.model).starts_with("EOS") {
            canon::read_eos(&mut session, &mut reading, None, canon_model.is_some())?;
        } else {
            usb::read_status(&mut session, &mut reading, vendor)?;
        }
        if reading.storage_issues.is_none() {
            reading.storage_issues = usb::read_storage(&mut session, vendor)?;
        }
    }
    Ok(reading)
}

struct Session(IPortableDevice);

impl Drop for Session {
    fn drop(&mut self) {
        let _ = unsafe { self.0.Close() };
    }
}

fn details(device: &IPortableDevice, reading: &mut Reading) -> Result<()> {
    let content = unsafe { device.Content()? };
    let properties = unsafe { content.Properties()? };
    let keys: IPortableDeviceKeyCollection = create(&PortableDeviceKeyCollection)?;
    for pid in [3, 4, 7, 8, 9] {
        unsafe { keys.Add(&key(DEVICE, pid))? };
    }
    let values = unsafe { properties.GetValues(w!("DEVICE"), &keys)? };
    let text = |pid| unsafe { values.GetStringValue(&key(DEVICE, pid)).ok().and_then(take_string) };
    if let Some(model) = text(8) {
        if reading.maker != "Canon" || !canon::model_key(&reading.model).starts_with("EOS")
            || canon::model_key(&model).starts_with("EOS")
        {
            reading.model = model;
        }
    }
    if let Some(maker) = text(7) { reading.maker = maker; }
    reading.serial = text(9);
    reading.firmware = text(3);
    reading.battery = unsafe { values.GetUnsignedIntegerValue(&key(DEVICE, 4)).ok().filter(|value| *value <= 100) };
    reading.storage_issues = storage(&content, &properties)?;
    Ok(())
}

fn storage(content: &IPortableDeviceContent, properties: &IPortableDeviceProperties) -> Result<Option<u8>> {
    let enumeration = unsafe { content.EnumObjects(0, w!("DEVICE"), None::<&IPortableDeviceValues>)? };
    let keys: IPortableDeviceKeyCollection = create(&PortableDeviceKeyCollection)?;
    for pid in [2, 11, 4, 5] {
        unsafe { keys.Add(&key(STORAGE, pid))? };
    }
    let mut issues = None;
    for _ in 0..16 {
        let mut object = [PWSTR::null()];
        let mut fetched = 0;
        let result = unsafe { enumeration.Next(&mut object, &mut fetched) };
        let id = take_string(object[0]);
        result.ok()?;
        if fetched == 0 { break; }
        let Some(id) = id else { continue };
        let id = HSTRING::from(id);
        let values = match unsafe { properties.GetValues(&id, &keys) } {
            Ok(values) => values,
            Err(error) if io_error(error.clone()).kind() == io::ErrorKind::NotConnected => return Err(error),
            Err(_) => continue,
        };
        let integer = |pid| unsafe { values.GetUnsignedIntegerValue(&key(STORAGE, pid)).ok() };
        let large = |pid| unsafe { values.GetUnsignedLargeIntegerValue(&key(STORAGE, pid)).ok() };
        if let Some(report) = usb::classify_storage(integer(2), integer(11), large(4), large(5)) {
            issues = Some(issues.unwrap_or_default() | report);
        }
    }
    Ok(issues)
}

impl Session {
    fn send(&self, command: u32, input: &IPortableDeviceValues) -> Result<IPortableDeviceValues> {
        unsafe {
            input.SetGuidValue(&key(COMMON, 1001), &MTP)?;
            input.SetUnsignedIntegerValue(&key(COMMON, 1002), command)?;
            let response = self.0.SendCommand(0, input)?;
            response.GetErrorValue(&key(COMMON, 1003))?.ok()?;
            Ok(response)
        }
    }

    fn exchange(&self, operation: u16, args: &[u32], mode: Direction<'_>) -> Result<Reply> {
        let request = values()?;
        let params: IPortableDevicePropVariantCollection = create(&PortableDevicePropVariantCollection)?;
        unsafe {
            for value in args {
                let param = PROPVARIANT {
                    Anonymous: PROPVARIANT_0 {
                        Anonymous: ManuallyDrop::new(PROPVARIANT_0_0 {
                            vt: VT_UI4, wReserved1: 0, wReserved2: 0, wReserved3: 0,
                            Anonymous: PROPVARIANT_0_0_0 { ulVal: *value },
                        }),
                    },
                };
                params.Add(&param)?;
            }
            request.SetUnsignedIntegerValue(&key(MTP, 1001), u32::from(operation))?;
            request.SetIPortableDevicePropVariantCollectionValue(&key(MTP, 1002), &params)?;
            let start = match mode {
                Direction::None => return Ok(Reply { code: response(&self.send(12, &request)?), data: None }),
                Direction::In => self.send(13, &request)?,
                Direction::Out(data) => {
                    request.SetUnsignedLargeIntegerValue(&key(MTP, 1007), data.len() as u64)?;
                    self.send(14, &request)?
                }
            };
            let Ok(context) = start.GetStringValue(&key(MTP, 1006)) else {
                return Ok(Reply { code: response(&start), data: None });
            };
            let Some(context) = take_string(context) else {
                return Ok(Reply { code: response(&start), data: None });
            };
            let transfer = values()?;
            transfer.SetStringValue(&key(MTP, 1006), &HSTRING::from(context))?;
            let data = match mode {
                Direction::In => {
                    let size = start.GetUnsignedLargeIntegerValue(&key(MTP, 1007)).ok()
                        .filter(|size| *size != u64::from(u32::MAX));
                    self.receive(&transfer, size)
                }
                Direction::Out(data) => self.write_data(&transfer, data),
                Direction::None => unreachable!(),
            };
            let end = self.send(17, &transfer);
            let data = data?;
            let code = response(&end?);
            Ok(Reply { code: if data.is_none() && code == OK { 0x2002 } else { code },
                data: if code == OK { data } else { None } })
        }
    }

    fn receive(&self, transfer: &IPortableDeviceValues, size: Option<u64>) -> Result<Option<Vec<u8>>> {
        if size.is_some_and(|size| size > MAX_DATA as u64) { return Ok(None); }
        let size = size.map(|value| value as usize);
        let mut payload = Vec::new();
        let chunk = [0u8; 16 * 1024];
        let buffer_key = key(MTP, 1012);
        while payload.len() < size.unwrap_or(MAX_DATA) {
            let ask = chunk.len().min(size.unwrap_or(MAX_DATA) - payload.len());
            unsafe {
                transfer.SetUnsignedIntegerValue(&key(MTP, 1008), ask as u32)?;
                transfer.SetBufferValue(&buffer_key, &chunk[..ask])?;
                let result = self.send(15, transfer)?;
                let actual = result.GetUnsignedIntegerValue(&key(MTP, 1009))? as usize;
                if actual > ask { return Ok(None); }
                if actual == 0 { return Ok(size.is_none().then_some(payload)); }
                let mut data = ptr::null_mut();
                let mut available = 0;
                let read = result.GetBufferValue(&buffer_key, &mut data, &mut available);
                let complete = !data.is_null() && (available as usize) >= actual && read.is_ok();
                if complete { payload.extend_from_slice(slice::from_raw_parts(data, actual)); }
                CoTaskMemFree(Some(data.cast()));
                read?;
                if !complete { return Ok(None); }
                if size.is_none() && actual < ask { return Ok(Some(payload)); }
            }
        }
        Ok((size == Some(payload.len())).then_some(payload))
    }

    fn write_data(&self, transfer: &IPortableDeviceValues, data: &[u8]) -> Result<Option<Vec<u8>>> {
        unsafe {
            transfer.SetUnsignedIntegerValue(&key(MTP, 1010), data.len() as u32)?;
            transfer.SetBufferValue(&key(MTP, 1012), data)?;
            let reply = self.send(16, transfer)?;
            let sent = reply.GetUnsignedIntegerValue(&key(MTP, 1011))? as usize;
            Ok((sent == data.len()).then(Vec::new))
        }
    }
}

fn response(values: &IPortableDeviceValues) -> u16 {
    unsafe { values.GetUnsignedIntegerValue(&key(MTP, 1003)).ok() }
        .and_then(|code| u16::try_from(code).ok()).unwrap_or(0x2005)
}

impl Transport for Session {
    fn execute(&mut self, operation: u16, args: &[u32], mode: Direction<'_>) -> io::Result<Reply> {
        match self.exchange(operation, args, mode).map_err(io_error) {
            Err(error) if error.kind() != io::ErrorKind::NotConnected => Ok(Reply { code: 0x2005, data: None }),
            result => result,
        }
    }
}
