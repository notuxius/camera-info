using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CameraInspector;

internal static class WifiCamera
{
    private const string EosService = "urn:schemas-canon-com:service:ICPO-WFTEOSSystemService:1";
    private const ushort GetDeviceInfo = 0x1001, OpenSession = 0x1002, CloseSession = 0x1003;
    private const ushort GetProperty = 0x1015, BatteryLevel = 0x5001;
    private const ushort GetDeviceInfoEx = 0x9108, SetRemoteMode = 0x9114, SetEventMode = 0x9115;
    private const ushort GetEvent = 0x9116, RequestProperty = 0x9127;
    private static readonly Lazy<Guid> ClientId = new(LoadClientId);

    internal static IPAddress? ParseAddress(string text)
    {
        string address = text.Trim();
        return IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork &&
               ip.ToString() == address && ip.GetAddressBytes()[0] is > 0 and < 224 ? ip : null;
    }

    internal static async Task<IReadOnlyList<IPAddress>> DiscoverAsync(CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        var group = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        var request = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\n" +
            $"MAN: \"ssdp:discover\"\r\nMX: 1\r\nST: {EosService}\r\n\r\n");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var addresses = new HashSet<IPAddress>();
        try
        {
            await socket.SendAsync(request, group, deadline.Token).ConfigureAwait(false);
            while (addresses.Count < 16)
            {
                var reply = await socket.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (IsDiscoveryReply(reply.Buffer))
                    addresses.Add(reply.RemoteEndPoint.Address);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return addresses.OrderBy(address => address.ToString(), StringComparer.Ordinal).ToArray();
    }

    internal static bool IsDiscoveryReply(byte[] data)
    {
        if (data.Length > 4096)
            return false;
        string[] lines = Encoding.ASCII.GetString(data).Split("\r\n");
        return lines[0].StartsWith("HTTP/1.1 200 ", StringComparison.OrdinalIgnoreCase) &&
            lines.Skip(1).TakeWhile(line => line.Length > 0).Any(line =>
                line.StartsWith("ST:", StringComparison.OrdinalIgnoreCase) &&
                line[3..].Trim().Equals(EosService, StringComparison.OrdinalIgnoreCase));
    }

    internal static Task<CameraReading> ReadAsync(IPAddress address, CancellationToken token) =>
        ReadAsync(new IPEndPoint(address, PtpIpConnection.Port), ClientId.Value, token);

    internal static async Task<CameraReading> ReadAsync(IPEndPoint endpoint, Guid clientId, CancellationToken token)
    {
        using var connection = new PtpIpConnection();
        await connection.InitializeAsync(endpoint, clientId, token).ConfigureAwait(false);
        using var events = CancellationTokenSource.CreateLinkedTokenSource(token);
        var listener = connection.ListenForEventsAsync(events.Token);
        bool sessionOpen = false;
        try
        {
            var info = await ReadInfoAsync(connection, token).ConfigureAwait(false);
            // An EOS R8 field report requires DeviceInfo before OpenSession with ID 0x41.
            uint session = info is { Reading.Model: var model } && CanonShutter.ModelKey(model) == "EOSR8" ? 0x41u : 1u;
            var response = await connection.ExecuteAsync(OpenSession, token, session).ConfigureAwait(false);
            if (response.Code != PtpIpConnection.Ok)
                throw new IOException($"The camera declined the PTP session (0x{response.Code:X4}).");
            sessionOpen = true;

            info = await ReadInfoAsync(connection, token).ConfigureAwait(false) ?? info;
            if (info is null)
                throw new IOException("The camera did not return PTP device information.");

            var reading = info.Reading;
            if (info.Operations.Contains(GetProperty) && info.Properties.Contains(BatteryLevel) &&
                (await connection.ExecuteAsync(GetProperty, token, BatteryLevel).ConfigureAwait(false)).Data is [<= 100 and var battery])
                reading = reading with { Battery = battery };

            if (reading.Maker.StartsWith("Canon", StringComparison.OrdinalIgnoreCase) &&
                reading.Model.Contains("EOS", StringComparison.OrdinalIgnoreCase))
                reading = await ReadEosAsync(connection, info, reading, token).ConfigureAwait(false);
            return reading;
        }
        finally
        {
            if (sessionOpen)
                await connection.TryCleanupAsync(CloseSession).ConfigureAwait(false);
            events.Cancel();
            await listener.ConfigureAwait(false);
        }
    }

    private static async Task<CameraReading> ReadEosAsync(PtpIpConnection connection, DeviceInfo info,
        CameraReading reading, CancellationToken token)
    {
        var protocol = CanonShutter.ProtocolFor(reading.Model);
        var eos = new CanonEosEvents(protocol);
        if (info.Operations.Contains(GetProperty) && protocol != CanonCounterProtocol.Unknown &&
            (await connection.ExecuteAsync(GetProperty, token, CanonShutter.LegacyProperty).ConfigureAwait(false)).Data is { Length: 4 } data)
        {
            uint legacy = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (legacy != uint.MaxValue)
                eos.Legacy = legacy;
        }

        bool remote = false, events = false;
        try
        {
            if (info.Operations.Contains(GetEvent) && info.Operations.Contains(SetEventMode))
            {
                if (info.Operations.Contains(SetRemoteMode))
                    remote = (await connection.ExecuteAsync(SetRemoteMode, token, 1).ConfigureAwait(false)).Code == PtpIpConnection.Ok;
                events = (await connection.ExecuteAsync(SetEventMode, token, 1).ConfigureAwait(false)).Code == PtpIpConnection.Ok;
                if (events)
                {
                    if (info.Operations.Contains(GetDeviceInfoEx))
                        await connection.ExecuteAsync(GetDeviceInfoEx, token).ConfigureAwait(false);
                    await PollEosAsync(connection, eos, 3, token).ConfigureAwait(false);
                    if (info.Operations.Contains(RequestProperty))
                    {
                        if (!eos.HasMechanical && protocol == CanonCounterProtocol.ReleaseEvent)
                            await RequestEosPropertyAsync(connection, eos, CanonShutter.ReleaseProperty, token).ConfigureAwait(false);
                        if (!eos.HasMechanical && !eos.Legacy.HasValue)
                            await RequestEosPropertyAsync(connection, eos, CanonShutter.LegacyProperty, token).ConfigureAwait(false);
                        if (!eos.HasMechanical && !eos.ScalarRelease.HasValue && protocol != CanonCounterProtocol.ReleaseEvent)
                            await RequestEosPropertyAsync(connection, eos, CanonShutter.ReleaseProperty, token).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            if (events)
                await connection.TryCleanupAsync(SetEventMode, 0).ConfigureAwait(false);
            if (remote)
                await connection.TryCleanupAsync(SetRemoteMode, 0).ConfigureAwait(false);
        }
        return reading with { Count = eos.Result, DeviceErrorCode = eos.DeviceErrorCode, Battery = reading.Battery ?? eos.Battery };
    }

    private static async Task RequestEosPropertyAsync(PtpIpConnection connection, CanonEosEvents eos, uint property, CancellationToken token)
    {
        if ((await connection.ExecuteAsync(RequestProperty, token, property).ConfigureAwait(false)).Code == PtpIpConnection.Ok)
            await PollEosAsync(connection, eos, 4, token).ConfigureAwait(false);
    }

    private static async Task PollEosAsync(PtpIpConnection connection, CanonEosEvents eos, int attempts, CancellationToken token)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (i > 0)
                await Task.Delay(160, token).ConfigureAwait(false);
            if ((await connection.ExecuteAsync(GetEvent, token).ConfigureAwait(false)).Data is not { } data)
                return;
            eos.Parse(data);
            if (eos.HasMechanical && eos.Battery.HasValue)
                return;
        }
    }

    private static async Task<DeviceInfo?> ReadInfoAsync(PtpIpConnection connection, CancellationToken token)
    {
        var response = await connection.ExecuteAsync(GetDeviceInfo, token).ConfigureAwait(false);
        return response.Data is { } data ? DeviceInfo.Parse(data) : null;
    }

    private sealed record DeviceInfo(CameraReading Reading, ushort[] Operations, ushort[] Properties)
    {
        internal static DeviceInfo Parse(byte[] data)
        {
            var reader = new DeviceInfoReader(data);
            reader.Take(8); // Standard version, vendor extension ID and vendor extension version.
            reader.ReadString(); // Vendor extension description.
            reader.Take(2); // Functional mode.
            ushort[] operations = reader.ReadCodes();
            reader.SkipCodes(); // Events.
            ushort[] properties = reader.ReadCodes();
            reader.SkipCodes(); // Capture formats.
            reader.SkipCodes(); // Image formats.
            string maker = reader.ReadString() ?? "—";
            string model = reader.ReadString() ?? CameraDevice.UnknownName;
            string? firmware = reader.ReadString();
            string? serial = reader.ReadString();
            if (maker.StartsWith("Canon", StringComparison.OrdinalIgnoreCase) && model.StartsWith("EOS ", StringComparison.OrdinalIgnoreCase))
                model = "Canon " + model;
            return new(new(model, maker, serial, firmware, null, null, ReadingSource.Wifi), operations, properties);
        }
    }

    private ref struct DeviceInfoReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;

        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count > _data.Length)
                throw new IOException("Truncated PTP device information.");
            var value = _data[..count];
            _data = _data[count..];
            return value;
        }

        internal string? ReadString()
        {
            int characters = Take(1)[0];
            if (characters == 0)
                return null;
            var value = Take(characters * 2);
            if (value[^2] != 0 || value[^1] != 0)
                throw new IOException("Invalid PTP device string.");
            var cleaned = new string(Encoding.Unicode.GetString(value[..^2]).Where(c => !char.IsControl(c)).ToArray()).Trim();
            return cleaned.Length == 0 ? null : cleaned;
        }

        private ReadOnlySpan<byte> TakeCodes()
        {
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
            if (count > _data.Length / 2)
                throw new IOException("Truncated PTP device capabilities.");
            return Take((int)count * 2);
        }

        internal void SkipCodes() => TakeCodes();

        internal ushort[] ReadCodes()
        {
            var data = TakeCodes();
            var codes = new ushort[data.Length / 2];
            for (int i = 0; i < codes.Length; i++)
                codes[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);
            return codes;
        }
    }

    private static Guid LoadClientId()
    {
        string path = Path.Combine(Path.GetDirectoryName(Languages.PreferencePath)!, "wifi-client.txt");
        try
        {
            if (Guid.TryParse(File.ReadAllText(path), out var saved) && saved != Guid.Empty)
                return saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(error.Message);
        }
        var id = Guid.NewGuid();
        PreferenceFile.Write(path, id.ToString());
        return id;
    }
}
