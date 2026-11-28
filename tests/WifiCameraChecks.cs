using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CameraInspector;

internal static partial class Program
{
    private static async Task CheckWifiCamera()
    {
        Check(WifiCamera.ParseAddress(" 192.168.1.42 ")?.ToString() == "192.168.1.42" &&
            WifiCamera.ParseAddress("192.168.1.42:15740") is null && WifiCamera.ParseAddress("239.255.255.250") is null,
            "Wi-Fi scans accept dotted IPv4 addresses and reject ports and multicast addresses");
        Check(WifiCamera.IsDiscoveryReply("HTTP/1.1 200 OK\r\nST: urn:schemas-canon-com:service:ICPO-WFTEOSSystemService:1\r\n\r\n"u8.ToArray()) &&
            !WifiCamera.IsDiscoveryReply("HTTP/1.1 200 OK\r\nST: upnp:rootdevice\r\n\r\n"u8.ToArray()),
            "SSDP discovery accepts the Canon PC pairing service only");

        var id = Guid.Parse("24e67527-7f48-4bc0-bb22-2859cb3cd92f");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var camera = new TcpListener(IPAddress.Loopback, 0);
        camera.Start();
        var scan = WifiCamera.ReadAsync((IPEndPoint)camera.LocalEndpoint, id, deadline.Token);
        using var commandClient = await camera.AcceptTcpClientAsync(deadline.Token);
        var command = commandClient.GetStream();
        var init = await ReadWifiPacket(command, 1, deadline.Token);
        Check(new Guid(init.AsSpan(0, 16), bigEndian: true) == id &&
            Encoding.Unicode.GetString(init.AsSpan(16, init.Length - 20)) == "Camera Inspector\0" &&
            BinaryPrimitives.ReadUInt32LittleEndian(init.AsSpan(init.Length - 4)) == 0x00010000,
            "The command handshake sends the saved identity and protocol version");

        var ack = new byte[26];
        BinaryPrimitives.WriteUInt32LittleEndian(ack, 32);
        await SendWifiPacket(command, 2, ack, deadline.Token);
        using var eventClient = await camera.AcceptTcpClientAsync(deadline.Token);
        var events = eventClient.GetStream();
        Check(BinaryPrimitives.ReadUInt32LittleEndian(await ReadWifiPacket(events, 3, deadline.Token)) == 32,
            "The event handshake uses the command connection number");
        await SendWifiPacket(events, 4, [], deadline.Token);
        await SendWifiPacket(events, 13, [], deadline.Token);
        await ReadWifiPacket(events, 14, deadline.Token);

        byte[] info = WifiDeviceInfo();
        var battery = new byte[16];
        WriteRecord(battery, 0xD111, 4);
        (ushort Opcode, uint? Argument, byte[]? Data, ushort Response)[] operations =
        [
            (0x1001, null, info, 0x2001),
            (0x1002, 0x41, null, 0x2001),
            (0x1001, null, info, 0x2001),
            (0x1015, CanonShutter.LegacyProperty, null, 0x200A),
            (0x9114, 1, null, 0x2001),
            (0x9115, 1, null, 0x2001),
            (0x9108, null, [], 0x2001),
            (0x9116, null, battery, 0x2001),
            (0x9116, null, [], 0x2001),
            (0x9116, null, [], 0x2001),
            (0x9127, CanonShutter.ReleaseProperty, null, 0x2001),
            (0x9116, null, ReleaseEvent(87_000, 0), 0x2001),
            (0x9115, 0, null, 0x2001),
            (0x9114, 0, null, 0x2001),
            (0x1003, null, null, 0x2001)
        ];
        for (int index = 0; index < operations.Length; index++)
        {
            var (opcode, argument, data, response) = operations[index];
            var request = await ReadWifiPacket(command, 6, deadline.Token);
            uint transaction = (uint)Math.Max(0, index - 1);
            Check(request.Length == (argument.HasValue ? 14 : 10) &&
                BinaryPrimitives.ReadUInt32LittleEndian(request) == 1 &&
                BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(4)) == opcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(6)) == transaction &&
                (!argument.HasValue || BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(10)) == argument),
                $"Wi-Fi scan issues PTP operation 0x{opcode:X4} in order");
            if (data is not null)
            {
                await SendWifiPacket(command, 9, [.. BitConverter.GetBytes(transaction), .. BitConverter.GetBytes((ulong)data.Length)], deadline.Token);
                int split = data.Length / 2;
                if (split > 0)
                    await SendWifiPacket(command, 10, [.. BitConverter.GetBytes(transaction), .. data.AsSpan(0, split)], deadline.Token);
                await SendWifiPacket(command, 12, [.. BitConverter.GetBytes(transaction), .. data.AsSpan(split)], deadline.Token);
            }
            await SendWifiPacket(command, 7, [.. BitConverter.GetBytes(response), .. BitConverter.GetBytes(transaction)], deadline.Token);
        }

        var reading = await scan;
        Check(reading is { Source: ReadingSource.Wifi, Model: "Canon EOS R8", Maker: "Canon.Inc", Serial: "WIFI123",
            Firmware: "1.6.0", Battery: 75, DeviceErrorCode: 0, Count: { Value: 87_000, IsUpperBound: true, Kind: CountKind.Mechanical } },
            "The live Wi-Fi read decodes fragmented PTP device information and Canon EOS events");

        var rejected = WifiCamera.ReadAsync((IPEndPoint)camera.LocalEndpoint, id, deadline.Token);
        using var refused = await camera.AcceptTcpClientAsync(deadline.Token);
        await ReadWifiPacket(refused.GetStream(), 1, deadline.Token);
        await SendWifiPacket(refused.GetStream(), 5, BitConverter.GetBytes(1u), deadline.Token);
        try { await rejected; throw new Exception("A rejected camera handshake returned a reading."); }
        catch (IOException) { }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var cancelled = WifiCamera.ReadAsync((IPEndPoint)camera.LocalEndpoint, id, cancel.Token);
        using var pending = await camera.AcceptTcpClientAsync(deadline.Token);
        await ReadWifiPacket(pending.GetStream(), 1, deadline.Token);
        cancel.Cancel();
        try { await cancelled; throw new Exception("A cancelled pairing returned a reading."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("Wi-Fi PTP/IP discovery parsing, pairing, Canon camera reads, and cancellation checks passed.");
    }

    private static byte[] WifiDeviceInfo()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode);
        writer.Write((ushort)100);
        writer.Write(11u);
        writer.Write((ushort)100);
        writer.Write((byte)0);
        writer.Write((ushort)0);
        ushort[] operations = [0x1001, 0x1002, 0x1003, 0x1015, 0x9108, 0x9114, 0x9115, 0x9116, 0x9127];
        writer.Write((uint)operations.Length);
        foreach (ushort operation in operations)
            writer.Write(operation);
        for (int i = 0; i < 4; i++)
            writer.Write(0u);
        foreach (string value in new[] { "Canon.Inc", "EOS R8", "1.6.0", "WIFI123" })
        {
            writer.Write((byte)(value.Length + 1));
            writer.Write(Encoding.Unicode.GetBytes(value + "\0"));
        }
        return stream.ToArray();
    }

    private static async Task<byte[]> ReadWifiPacket(NetworkStream stream, uint type, CancellationToken token)
    {
        byte[] header = new byte[8];
        await stream.ReadExactlyAsync(header, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        Check(size is >= 8 and <= 1024 && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) == type,
            $"Received expected PTP/IP packet type {type}");
        byte[] payload = new byte[size - 8];
        await stream.ReadExactlyAsync(payload, token);
        return payload;
    }

    private static async Task SendWifiPacket(NetworkStream stream, uint type, byte[] payload, CancellationToken token)
    {
        byte[] packet = [.. BitConverter.GetBytes(payload.Length + 8), .. BitConverter.GetBytes(type), .. payload];
        await stream.WriteAsync(packet.AsMemory(0, 3), token);
        await stream.WriteAsync(packet.AsMemory(3), token);
    }

    private static void CheckWifiDashboard(MainWindow window, string history, string? renderFolder)
    {
        var usb = ExampleReading(87_000) with { Count = new(87_000, CountKind.Mechanical, CanonShutter.LegacyProperty) };
        var wifi = usb with { Count = new(86_000, CountKind.Mechanical, CanonShutter.LegacyProperty), Source = ReadingSource.Wifi };
        ShutterHistory.TryRecord(usb, history);
        var comparison = ShutterHistory.TryRecord(wifi, history);
        window.ShowLiveReading(wifi, comparison);
        Check(comparison?.PreviousCount == 87_000 && Label(window, "SourceValue") == "Wi-Fi" &&
            Label(window, "ConnectionText") == "Wi-Fi camera connected" && Label(window, "CountText") == "86,000" &&
            Label(window, "CameraWarningsText").Contains("A decrease was recorded in the shutter count."),
            "The dashboard displays Wi-Fi as a live reading and shares comparable shutter history with USB");

        window.ShowLiveReading(wifi with { Count = null }, null);
        Check(Label(window, "UsageNote").Contains("over this connection"), "Missing Wi-Fi counters use the live connection status");
        var language = Languages.Options.Single(option => option.Code == "en");
        var dialog = new WifiScanWindow(language, Languages.Catalog(language), IPAddress.Parse("192.168.1.42"));
        Check(dialog.Address?.ToString() == "192.168.1.42" && ((Button)dialog.FindName("ConnectButton")).IsDefault,
            "The Wi-Fi dialog offers direct entry and restores the last address");
        if (renderFolder is not null)
        {
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new Size(494, double.PositiveInfinity));
            Render(content, renderFolder, "wifi-scan", 494, Math.Ceiling(content.DesiredSize.Height));
        }
        dialog.Close();
    }
}
