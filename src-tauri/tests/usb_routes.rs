
use camera_inspector::{
    camera::{CountKind, Reading, ShutterCount, Source},
    canon::{self, LEGACY_PROPERTY, RELEASE_PROPERTY},
    ptp::{Direction, Reply, Transport, OK},
    usb,
};
use std::{collections::VecDeque, io};

struct Step {
    operation: u16,
    args: Vec<u32>,
    result: io::Result<Reply>,
    mode: u8,
}

struct Camera(VecDeque<Step>);

impl Camera {
    fn expect(steps: Vec<Step>) -> Self {
        Self(steps.into())
    }

    fn complete(self) {
        assert!(self.0.is_empty(), "camera session left commands pending");
    }
}

impl Transport for Camera {
    fn execute(&mut self, operation: u16, args: &[u32], direction: Direction<'_>) -> io::Result<Reply> {
        let next = self.0.pop_front().unwrap_or_else(|| panic!("unexpected PTP operation {operation:#06x}"));
        assert_eq!(operation, next.operation);
        assert_eq!(args, next.args);
        let mode = match direction {
            Direction::None => 0,
            Direction::In => 1,
            Direction::Out(bytes) => {
                let name = if bytes.starts_with(b"MonOpen\0") {
                    assert_eq!(bytes.len(), 36);
                    2
                } else if bytes.starts_with(b"MonReadAndGetData\0") {
                    assert_eq!(bytes.len(), 86);
                    3
                } else {
                    assert!(bytes.starts_with(b"MonClose\0"));
                    assert_eq!(bytes.len(), 37);
                    4
                };
                name
            }
        };
        assert_eq!(mode, next.mode);
        next.result
    }
}

fn read(operation: u16, args: &[u32], data: Option<Vec<u8>>) -> Step {
    let code = if data.is_some() { OK } else { 0x2005 };
    Step { operation, args: args.to_vec(), result: Ok(Reply { code, data }), mode: 1 }
}

fn control(operation: u16, argument: u32, code: u16) -> Step {
    Step { operation, args: vec![argument], result: Ok(Reply { code, data: None }), mode: 0 }
}

fn monitor(argument: u32, mode: u8, code: u16) -> Step {
    Step { operation: 0x9052, args: vec![0, argument], result: Ok(Reply { code, data: None }), mode }
}

fn release(value: u32) -> Vec<u8> {
    [28, 0xC189, RELEASE_PROPERTY, 16, 1, value, value + 1000]
        .into_iter().flat_map(u32::to_le_bytes).collect()
}

fn camera(product: u16) -> Reading {
    Reading::new(canon::usb_model(Some((0x04a9, product))).unwrap(), "Canon".into(), Source::Usb)
}

#[test]
fn emulates_all_twenty_four_canon_routes_at_the_ptp_boundary() {
    for product in [0x3199, 0x319a, 0x323a, 0x3250, 0x3253, 0x326f] {
        let mut reading = camera(product);
        let mut transport = Camera::expect(vec![read(0x1015, &[LEGACY_PROPERTY], Some(301_123u32.to_le_bytes().to_vec()))]);
        canon::read_eos(&mut transport, &mut reading, None, true).unwrap();
        transport.complete();
        assert_eq!(reading.count, Some(ShutterCount::live(301_123, CountKind::Mechanical, LEGACY_PROPERTY, false)));
    }
    for product in [0x3281, 0x3294, 0x32ca] {
        let mut reading = camera(product);
        let mut transport = Camera::expect(vec![
            monitor(0, 2, OK), monitor(1, 3, OK),
            read(0x9053, &[0, 0, 1], Some(vec![2, 226, 0, 0, 124, 0, 0, 0, 0, 0])),
            monitor(0, 4, OK),
        ]);
        canon::read_eos(&mut transport, &mut reading, None, true).unwrap();
        transport.complete();
        assert_eq!(reading.count, Some(ShutterCount::live(189_052, CountKind::Mechanical, 0x1001, false)));
    }
    for product in [0x32da, 0x32e2, 0x32ea, 0x32f1, 0x32f4, 0x32f5, 0x32f7, 0x32f8, 0x32f9,
        0x330b, 0x330c, 0x330d, 0x3312, 0x3314, 0x3323]
    {
        let mut reading = camera(product);
        let mut transport = Camera::expect(vec![
            read(0x1015, &[LEGACY_PROPERTY], Some(500_000u32.to_le_bytes().to_vec())),
            control(0x9114, 1, OK), control(0x9115, 1, OK),
            read(0x9108, &[], Some(vec![])), read(0x9116, &[], Some(release(250_000))),
            control(0x9115, 0, OK), control(0x9114, 0, OK),
        ]);
        canon::read_eos(&mut transport, &mut reading, None, true).unwrap();
        transport.complete();
        assert_eq!(reading.count, Some(ShutterCount::live(250_000, CountKind::Mechanical, RELEASE_PROPERTY, true)));
    }
}

#[test]
fn rejected_diagnostics_fall_back_and_missing_readings_stay_unavailable() {
    let mut reading = camera(0x3294);
    let mut transport = Camera::expect(vec![
        monitor(0, 2, OK), monitor(1, 3, 0x2002), monitor(0, 4, OK),
        read(0x1015, &[LEGACY_PROPERTY], Some(88_000u32.to_le_bytes().to_vec())),
    ]);
    canon::read_eos(&mut transport, &mut reading, None, true).unwrap();
    transport.complete();
    assert_eq!(reading.count, Some(ShutterCount::live(88_000, CountKind::Unspecified, LEGACY_PROPERTY, false)));

    let mut reading = camera(0x323a);
    let mut transport = Camera::expect(vec![
        read(0x1015, &[LEGACY_PROPERTY], Some(u32::MAX.to_le_bytes().to_vec())),
        control(0x9114, 1, OK), control(0x9115, 1, 0x2002), control(0x9114, 0, OK),
    ]);
    canon::read_eos(&mut transport, &mut reading, None, true).unwrap();
    transport.complete();
    assert!(reading.count.is_none());
}

#[test]
fn queries_only_read_only_ptp_storage_operations() {
    let mut ids = Vec::from(2u32.to_le_bytes());
    ids.extend(0x00010000u32.to_le_bytes());
    ids.extend(0x00010001u32.to_le_bytes());
    let mut info = vec![0u8; 28];
    info[0] = 4;
    info[4] = 1;
    info[6..14].copy_from_slice(&1024u64.to_le_bytes());
    let mut transport = Camera::expect(vec![
        read(0x1004, &[], Some(ids)), read(0x1005, &[0x00010001], Some(info)),
    ]);
    assert_eq!(usb::read_storage(&mut transport, 0x04b0).unwrap(), Some(usb::FULL | usb::READ_ONLY));
    transport.complete();
    assert_eq!(usb::classify_storage(Some(2), Some(1), Some(32), Some(0)), None);
}
