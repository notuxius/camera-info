using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace CameraInspector;

internal enum CanonCounterProtocol { Unknown, Legacy, Monitor, ReleaseEvent }

internal static class CanonShutter
{
    internal const uint LegacyProperty = 0xD1AC;
    internal const uint ReleaseProperty = 0xD167;
    private const uint MonitorShutterAddress = 0x1001;
    internal const uint ReleaseStep = 1_000;
    private const int MonitorReplyLength = 10;

    // Canon USB product IDs in libgphoto2. WPD sometimes supplies only “Canon Digital Camera”.
    internal static string? UsbModel(string id)
    {
        var match = Regex.Match(id, @"\bVID_04A9&PID_([0-9A-F]{4})(?![0-9A-F])", RegexOptions.IgnoreCase);
        string? model = match.Groups[1].Value.ToUpperInvariant() switch
        {
            "3199" => "5D Mark II",
            "319A" => "7D",
            "323A" => "5D Mark III",
            "3250" => "6D",
            "3253" => "70D",
            "326F" => "7D Mark II",
            "3281" => "5D Mark IV",
            "3294" => "80D",
            "32CA" => "6D Mark II",
            "32DA" => "R",
            "32E2" => "RP",
            "32EA" => "90D",
            "32F1" => "850D",
            "32F4" => "R5",
            "32F5" => "R6",
            "32F7" => "R7",
            "32F8" => "R10",
            "32F9" => "M50 Mark II",
            "32FC" => "R3",
            "3303" => "R5 C",
            "330B" => "R6 Mark II",
            "330C" => "R8",
            "330D" => "R50",
            "3312" => "R100",
            "3313" => "R1",
            "3314" => "R5 Mark II",
            "3320" => "R50 V",
            "3323" => "R6 Mark III",
            _ => null
        };
        return model is null ? null : $"Canon EOS {model}";
    }

    internal static string ModelKey(string model)
    {
        string key = Regex.Replace(model.ToUpperInvariant(), "[^A-Z0-9]", "");
        if (key.StartsWith("CANON", StringComparison.Ordinal))
            key = key[5..];

        return key switch
        {
            "EOSREBELT8I" or "EOSKISSX10I" => "EOS850D",
            "EOS6DM2" => "EOS6DMARKII",
            "EOS5RM2" or "EOSR5M2" or "EOS5RMARKII" => "EOSR5MARKII",
            "EOSR6M2" => "EOSR6MARKII",
            "EOSR6M3" => "EOSR6MARKIII",
            "EOSM50M2" or "EOSKISSM2" => "EOSM50MARKII",
            _ => key
        };
    }

    internal static CanonCounterProtocol ProtocolFor(string model) => ModelKey(model) switch
    {
        "EOS5DMARKII" or "EOS5DMARKIII" or "EOS6D" or "EOS7D" or "EOS7DMARKII" or "EOS70D" => CanonCounterProtocol.Legacy,
        "EOS5DMARKIV" or "EOS6DMARKII" or "EOS80D" => CanonCounterProtocol.Monitor,
        "EOS90D" or "EOS850D" or "EOSM50MARKII" or "EOSR" or "EOSRP" or "EOSR5" or "EOSR5MARKII" or "EOSR6"
            or "EOSR6MARKII" or "EOSR6MARKIII" or "EOSR7" or "EOSR8" or "EOSR10" or "EOSR50" or "EOSR100" => CanonCounterProtocol.ReleaseEvent,
        _ => CanonCounterProtocol.Unknown
    };

    internal static ShutterCount? DecodeRelease(ReadOnlySpan<byte> payload)
    {
        // The EOS release structure holds size, version, mechanical bound and total bound.
        if (payload.Length != 16 || BinaryPrimitives.ReadUInt32LittleEndian(payload) != 16 ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]) != 1)
            return null;

        uint mechanical = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]);
        if (mechanical > total || mechanical % ReleaseStep != 0 || total % ReleaseStep != 0)
            return null;

        return new(mechanical, CountKind.Mechanical, ReleaseProperty, IsUpperBound: true);
    }

    internal static byte[] MonitorSessionPayload(bool open)
    {
        ReadOnlySpan<byte> name = open ? "MonOpen\0"u8 : "MonClose\0"u8;
        var payload = new byte[name.Length + sizeof(uint) + 24];
        name.CopyTo(payload);
        return payload;
    }

    internal static byte[] MonitorShutterPayload()
    {
        ReadOnlySpan<byte> name = "MonReadAndGetData\0"u8;
        ReadOnlySpan<uint> arguments = [2, MonitorShutterAddress, MonitorReplyLength];
        const int recordSize = 20;
        var payload = new byte[name.Length + sizeof(uint) + arguments.Length * recordSize + 4];
        name.CopyTo(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(name.Length), (uint)arguments.Length);
        var record = payload.AsSpan(name.Length + sizeof(uint));
        foreach (uint argument in arguments)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(record, 2); // Integer argument tag.
            BinaryPrimitives.WriteUInt32LittleEndian(record[sizeof(uint)..], argument);
            record = record[recordSize..];
        }
        return payload;
    }

    internal static ShutterCount? DecodeMonitorShutter(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != MonitorReplyLength)
            return null;

        // 5DS R captures show both high bytes and a low-byte slot earlier than on the 5D Mark IV.
        uint high = BinaryPrimitives.ReadUInt16BigEndian(payload);
        uint low = 0;
        foreach (byte value in payload[2..])
        {
            if (value == 0)
                continue;
            if (low != 0)
                return null;
            low = value;
        }
        return new(high << 8 | low, CountKind.Mechanical, MonitorShutterAddress);
    }
}

internal sealed class CanonEosEvents(CanonCounterProtocol protocol)
{
    // Canon's ErrorForDisplay property in EOS property-change events.
    private const uint ErrorForDisplay = 0xD1C4;

    internal uint? Legacy { get; set; }
    internal uint? ScalarRelease { get; private set; }
    internal ShutterCount? MechanicalRelease { get; private set; }
    internal uint? DeviceErrorCode { get; private set; }
    internal uint? Battery { get; private set; }

    internal bool HasMechanical => MechanicalRelease.HasValue ||
        (protocol == CanonCounterProtocol.Legacy && Legacy.HasValue);

    internal ShutterCount? Result => MechanicalRelease ?? (Legacy is uint legacy
        ? new ShutterCount(legacy, protocol == CanonCounterProtocol.Legacy ? CountKind.Mechanical : CountKind.Unspecified, CanonShutter.LegacyProperty)
        : ScalarRelease is uint release ? new ShutterCount(release, CountKind.Unspecified, CanonShutter.ReleaseProperty) : null);

    internal void Parse(ReadOnlySpan<byte> data)
    {
        while (data.Length >= 8)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data);
            uint code = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            if (size < 8 || size > data.Length || (size == 8 && code == 0))
                return;

            if (size >= 16 && code == 0xC189)
            {
                uint property = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
                var payload = data[12..(int)size];
                if (payload.Length == 4)
                {
                    uint value = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    if (property == ErrorForDisplay)
                        DeviceErrorCode = value;
                    if (property == 0xD111)
                        Battery = value switch { 1 => 50u, 2 => 100u, 4 => 75u, 5 => 25u, _ => null };
                    if (value != uint.MaxValue)
                    {
                        if (property == CanonShutter.LegacyProperty)
                            Legacy = value;
                        if (property == CanonShutter.ReleaseProperty)
                            ScalarRelease = value;
                    }
                }
                else if (property == CanonShutter.ReleaseProperty && protocol == CanonCounterProtocol.ReleaseEvent
                    && CanonShutter.DecodeRelease(payload) is { } count)
                    MechanicalRelease = count;
            }

            data = data[(int)size..];
        }
    }
}
