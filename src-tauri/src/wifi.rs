
use crate::camera::{invalid, Reading, Source};
use crate::canon::{model_key, read_eos};
use crate::ptp::{le16, le32, le64, DeviceInfo, Direction, Reply, Transport, GET_DEVICE_INFO, GET_PROPERTY, MAX_DATA, OK};
use std::collections::BTreeSet;
use std::io::{self, Read, Write};
use std::net::{Ipv4Addr, Shutdown, SocketAddr, TcpStream, UdpSocket};
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};
use uuid::Uuid;

pub const PORT: u16 = 15740;
const EOS_SERVICE: &str = "urn:schemas-canon-com:service:ICPO-WFTEOSSystemService:1";
const IO_INTERVAL: Duration = Duration::from_millis(200);

pub fn parse_address(text: &str) -> Option<Ipv4Addr> {
    let ip: Ipv4Addr = text.trim().parse().ok()?;
    (1..224).contains(&ip.octets()[0]).then_some(ip)
}

pub fn discover(cancel: &AtomicBool) -> io::Result<Vec<Ipv4Addr>> {
    let socket = UdpSocket::bind((Ipv4Addr::UNSPECIFIED, 0))?;
    socket.set_read_timeout(Some(IO_INTERVAL))?;
    let request = format!(
        "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: {EOS_SERVICE}\r\n\r\n"
    );
    socket.send_to(request.as_bytes(), (Ipv4Addr::new(239, 255, 255, 250), 1900))?;
    let deadline = Instant::now() + Duration::from_secs(2);
    let mut addresses = BTreeSet::new();
    let mut bytes = [0; 4097];
    while addresses.len() < 16 && Instant::now() < deadline {
        check_cancel(cancel)?;
        match socket.recv_from(&mut bytes) {
            Ok((size, SocketAddr::V4(sender))) if is_discovery_reply(&bytes[..size]) => {
                addresses.insert(*sender.ip());
            }
            Ok(_) => {}
            Err(error) if retryable(&error) => {}
            Err(error) => return Err(error),
        }
    }
    Ok(addresses.into_iter().collect())
}

fn is_discovery_reply(reply: &[u8]) -> bool {
    if reply.len() > 4096 {
        return false;
    }
    let Ok(reply) = std::str::from_utf8(reply) else { return false };
    let mut lines = reply.split("\r\n");
    lines.next().is_some_and(|line| line.to_ascii_uppercase().starts_with("HTTP/1.1 200 ")) &&
        lines.take_while(|line| !line.is_empty()).any(|line| {
            line.split_once(':').is_some_and(|(header, value)| {
                header.eq_ignore_ascii_case("ST") && value.trim().eq_ignore_ascii_case(EOS_SERVICE)
            })
        })
}

pub fn read(endpoint: SocketAddr, client_id: Uuid, cancel: &AtomicBool) -> io::Result<Reading> {
    let mut connection = Connection::open(endpoint, client_id, cancel)?;
    let mut info = device_info(&mut connection)?;
    let session = if info.as_ref().is_some_and(|info| model_key(&info.reading.model) == "EOSR8") { 0x41 } else { 1 };
    let response = connection.execute(0x1002, &[session], Direction::None)?;
    if response.code != OK {
        return Err(invalid(&format!("Camera declined the PTP session (0x{:04X}).", response.code)));
    }

    let result = (|| {
        info = device_info(&mut connection)?.or(info.take());
        let info = info.ok_or_else(|| invalid("The camera did not return PTP device information."))?;
        let mut reading = info.reading;
        if info.operations.contains(&GET_PROPERTY) && info.properties.contains(&0x5001) {
            if let Some(data) = connection.read(GET_PROPERTY, &[0x5001])? {
                if let [battery @ 0..=100] = data.as_slice() {
                    reading.battery = Some(u32::from(*battery));
                }
            }
        }
        if reading.maker.to_ascii_uppercase().starts_with("CANON") && model_key(&reading.model).starts_with("EOS") {
            read_eos(&mut connection, &mut reading, Some(&info.operations), false)?;
        }
        Ok(reading)
    })();
    if connection.usable {
        connection.cancel = &NEVER_CANCELLED;
        connection.operation_timeout = Duration::from_secs(1);
        let _ = connection.control(0x1003, &[]);
    }
    result
}

fn device_info(connection: &mut Connection<'_>) -> io::Result<Option<DeviceInfo>> {
    connection.read(GET_DEVICE_INFO, &[])?
        .map(|data| DeviceInfo::parse(&data, Source::Wifi)).transpose()
}

static NEVER_CANCELLED: AtomicBool = AtomicBool::new(false);

struct Connection<'a> {
    command: TcpStream,
    event: TcpStream,
    event_listener: Option<JoinHandle<()>>,
    cancel: &'a AtomicBool,
    operation_timeout: Duration,
    transaction: u32,
    usable: bool,
}

impl<'a> Connection<'a> {
    fn open(address: SocketAddr, client_id: Uuid, cancel: &'a AtomicBool) -> io::Result<Self> {
        check_cancel(cancel)?;
        let mut command = connect(address)?;
        let mut init = Vec::from(client_id.into_bytes());
        init.extend("Camera Inspector\0".encode_utf16().flat_map(u16::to_le_bytes));
        init.extend(0x00010000_u32.to_le_bytes());

        let pairing = Instant::now() + Duration::from_secs(40);
        write_packet(&mut command, 1, &init, pairing, cancel)?;
        let (kind, reply) = read_packet(&mut command, pairing, cancel)?;
        if kind != 2 || reply.len() < 26 {
            return Err(invalid("The camera did not accept the PTP/IP command connection."));
        }

        let mut event = connect(address)?;
        let deadline = Instant::now() + Duration::from_secs(5);
        write_packet(&mut event, 3, &reply[..4], deadline, cancel)?;
        if read_packet(&mut event, deadline, cancel)? != (4, vec![]) {
            return Err(invalid("The camera did not accept the PTP/IP event connection."));
        }
        let event_stream = event.try_clone()?;
        let event_listener = thread::Builder::new().name("PTP/IP events".into()).spawn(|| listen(event_stream))?;
        Ok(Self {
            command, event, event_listener: Some(event_listener), cancel, operation_timeout: Duration::from_secs(5),
            transaction: 0, usable: true,
        })
    }

    fn read_reply(&mut self, deadline: Instant) -> io::Result<(u32, Vec<u8>)> {
        loop {
            let result = read_packet(&mut self.command, deadline, self.cancel)?;
            if result.0 != 13 {
                return Ok(result);
            }
            write_packet(&mut self.command, 14, &[], deadline, self.cancel)?;
        }
    }

    fn exchange(&mut self, operation: u16, args: &[u32]) -> io::Result<Reply> {
        let deadline = Instant::now() + self.operation_timeout;
        let transaction = if operation == 0x1002 { 0 } else { self.transaction };
        self.transaction = transaction.wrapping_add(1);
        let mut request = Vec::from(1u32.to_le_bytes());
        request.extend(operation.to_le_bytes());
        request.extend(transaction.to_le_bytes());
        request.extend(args.iter().copied().flat_map(u32::to_le_bytes));
        write_packet(&mut self.command, 6, &request, deadline, self.cancel)?;

        let (mut kind, mut payload) = self.read_reply(deadline)?;
        let mut data = None;
        if kind == 9 {
            if payload.len() != 12 || le32(&payload) != transaction || le64(&payload[4..]) > MAX_DATA as u64 {
                return Err(invalid("Invalid PTP/IP data header."));
            }
            let size = le64(&payload[4..]) as usize;
            let mut collected = Vec::with_capacity(size);
            loop {
                (kind, payload) = self.read_reply(deadline)?;
                if !matches!(kind, 10 | 12) || payload.len() < 4 || le32(&payload) != transaction
                    || payload.len() - 4 > size - collected.len()
                {
                    return Err(invalid("Invalid PTP/IP data packet."));
                }
                collected.extend_from_slice(&payload[4..]);
                if kind == 12 {
                    break;
                }
            }
            if collected.len() != size {
                return Err(invalid("The camera sent incomplete PTP/IP data."));
            }
            data = Some(collected);
            (kind, payload) = self.read_reply(deadline)?;
        }

        if kind != 7 || !(6..=26).contains(&payload.len()) || (payload.len() - 6) % 4 != 0
            || le32(&payload[2..]) != transaction
        {
            return Err(invalid("Invalid PTP/IP operation response."));
        }
        let code = le16(&payload);
        Ok(Reply { code, data: if code == OK { data } else { None } })
    }
}

impl Transport for Connection<'_> {
    fn execute(&mut self, operation: u16, args: &[u32], mode: Direction<'_>) -> io::Result<Reply> {
        if !self.usable || matches!(mode, Direction::Out(_)) {
            return Err(invalid("The PTP/IP command connection is unavailable for this operation."));
        }
        check_cancel(self.cancel)?;
        let result = self.exchange(operation, args);
        self.usable = result.is_ok();
        result
    }
}

impl Drop for Connection<'_> {
    fn drop(&mut self) {
        let _ = self.event.shutdown(Shutdown::Both);
        if let Some(listener) = self.event_listener.take() {
            let _ = listener.join();
        }
    }
}

fn connect(address: SocketAddr) -> io::Result<TcpStream> {
    let socket = TcpStream::connect_timeout(&address, Duration::from_secs(5))?;
    socket.set_nodelay(true)?;
    socket.set_read_timeout(Some(IO_INTERVAL))?;
    socket.set_write_timeout(Some(IO_INTERVAL))?;
    Ok(socket)
}

fn listen(mut stream: TcpStream) {
    while let Ok((kind, _)) = read_packet(
        &mut stream, Instant::now() + Duration::from_secs(120), &NEVER_CANCELLED,
    ) {
        match kind {
            8 => {}
            13 => {
                if write_packet(&mut stream, 14, &[], Instant::now() + Duration::from_secs(5), &NEVER_CANCELLED).is_err() {
                    break;
                }
            }
            _ => break,
        }
    }
}

fn read_packet(stream: &mut TcpStream, deadline: Instant, cancel: &AtomicBool) -> io::Result<(u32, Vec<u8>)> {
    let mut header = [0; 8];
    transfer(&mut header, deadline, cancel, |part| stream.read(part))?;
    let length = le32(&header) as usize;
    if !(8..=MAX_DATA + 12).contains(&length) {
        return Err(invalid("Invalid PTP/IP packet length."));
    }
    let mut payload = vec![0; length - 8];
    transfer(&mut payload, deadline, cancel, |part| stream.read(part))?;
    Ok((le32(&header[4..]), payload))
}

fn write_packet(
    stream: &mut TcpStream,
    kind: u32,
    payload: &[u8],
    deadline: Instant,
    cancel: &AtomicBool,
) -> io::Result<()> {
    let mut packet = Vec::from(((8 + payload.len()) as u32).to_le_bytes());
    packet.extend(kind.to_le_bytes());
    packet.extend(payload);
    transfer(&mut packet, deadline, cancel, |part| stream.write(part))
}

fn transfer(
    mut bytes: &mut [u8],
    deadline: Instant,
    cancel: &AtomicBool,
    mut handle: impl FnMut(&mut [u8]) -> io::Result<usize>,
) -> io::Result<()> {
    while !bytes.is_empty() {
        check_cancel(cancel)?;
        if Instant::now() >= deadline {
            return Err(io::Error::new(io::ErrorKind::TimedOut, "The camera timed out."));
        }
        match handle(bytes) {
            Ok(0) => return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "The camera closed the connection.")),
            Ok(length) => bytes = &mut bytes[length..],
            Err(error) if retryable(&error) => {}
            Err(error) => return Err(error),
        }
    }
    Ok(())
}

fn retryable(error: &io::Error) -> bool {
    matches!(error.kind(), io::ErrorKind::Interrupted | io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut)
}

fn check_cancel(cancel: &AtomicBool) -> io::Result<()> {
    if cancel.load(Ordering::Relaxed) {
        return Err(io::Error::new(io::ErrorKind::Interrupted, "Camera scan cancelled."));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::TcpListener;

    #[test]
    fn discovers_only_eos_pairing_and_validates_ip() {
        assert!(is_discovery_reply(format!("HTTP/1.1 200 OK\r\nST: {EOS_SERVICE}\r\n\r\n").as_bytes()));
        assert!(!is_discovery_reply(b"HTTP/1.1 200 OK\r\nST: upnp:rootdevice\r\n\r\n"));
        assert!(parse_address(" 192.168.1.42 ").is_some());
        assert!(parse_address("192.168.1.42:15740").is_none());
        assert!(parse_address("239.255.255.250").is_none());
    }

    #[test]
    fn reads_fragmented_data_and_closes_both_channels() {
        let server = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        let address = server.local_addr().unwrap();
        let id = Uuid::parse_str("24e67527-7f48-4bc0-bb22-2859cb3cd92f").unwrap();
        let emulator = thread::spawn(move || {
            let (mut command, _) = server.accept().unwrap();
            command.set_read_timeout(Some(IO_INTERVAL)).unwrap();
            let deadline = Instant::now() + Duration::from_secs(10);
            let read = |stream: &mut TcpStream| read_packet(stream, deadline, &NEVER_CANCELLED).unwrap();
            let write = |stream: &mut TcpStream, kind, data: &[u8]| {
                write_packet(stream, kind, data, deadline, &NEVER_CANCELLED).unwrap();
            };
            let (kind, init) = read(&mut command);
            assert_eq!(kind, 1);
            assert_eq!(&init[..16], id.as_bytes());
            let mut ack = vec![0; 26];
            ack[0] = 32;
            write(&mut command, 2, &ack);
            let (mut event, _) = server.accept().unwrap();
            event.set_read_timeout(Some(IO_INTERVAL)).unwrap();
            assert_eq!(read(&mut event), (3, vec![32, 0, 0, 0]));
            write(&mut event, 4, &[]);
            write(&mut event, 13, &[]);
            assert_eq!(read(&mut event), (14, vec![]));

            let mut info = vec![0; 8 + 1 + 2 + 5 * 4];
            for value in ["Canon.Inc", "EOS R8", "1.6.0", "WIFI123"] {
                info.push((value.len() + 1) as u8);
                info.extend(value.encode_utf16().chain([0]).flat_map(u16::to_le_bytes));
            }

            for (operation, transaction, data) in [
                (0x1001, 0u32, Some(info.as_slice())), (0x1002, 0, None),
                (0x1001, 1, Some(info.as_slice())), (0x1003, 2, None),
            ] {
                let (kind, request) = read(&mut command);
                assert_eq!(kind, 6);
                assert_eq!(le16(&request[4..]), operation);
                assert_eq!(le32(&request[6..]), transaction);
                if operation == 0x1002 {
                    assert_eq!(le32(&request[10..]), 0x41);
                }
                if let Some(data) = data {
                    let mut start = Vec::from(transaction.to_le_bytes());
                    start.extend((data.len() as u64).to_le_bytes());
                    write(&mut command, 9, &start);
                    let mut segment = Vec::from(transaction.to_le_bytes());
                    segment.extend(&data[..10]);
                    write(&mut command, 10, &segment);
                    segment.truncate(4);
                    segment.extend(&data[10..]);
                    write(&mut command, 12, &segment);
                }
                let mut response = Vec::from(OK.to_le_bytes());
                response.extend(transaction.to_le_bytes());
                write(&mut command, 7, &response);
            }
        });
        let reading = read(address, id, &NEVER_CANCELLED).unwrap();
        emulator.join().unwrap();
        assert_eq!(reading.model, "Canon EOS R8");
        assert_eq!(reading.serial.as_deref(), Some("WIFI123"));
        assert_eq!(reading.source, Source::Wifi);
    }
}
