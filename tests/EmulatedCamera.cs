using System.Runtime.InteropServices;
using CameraInspector;

internal sealed class EmulatedCamera(CameraDevice camera, params CameraExchange[] exchanges) :
    IPortableDevice, IPortableDeviceContent, IPortableDeviceProperties
{
    private static readonly Guid Client = new("204d9f0c-2292-4080-9f42-40664e70f859");
    private static readonly Guid Device = new("26d4979a-e643-4626-9e2b-736dc0c92fdc");
    private static readonly Guid Common = new("f0422a9c-5dc8-4440-b5bd-5df28835658a");
    private static readonly Guid Mtp = new("4d545058-1a2e-4106-a357-771e0819fc56");
    private static readonly Guid ValuesClass = new("0c15d503-d017-47ce-9016-7b3f978721cc");
    private const int AccessDenied = unchecked((int)0x80070005);

    private readonly Queue<CameraExchange> _remaining = new(exchanges);
    private CameraExchange? _active;
    private string _context = "";
    private int _offset;
    private int _readCalls;
    private bool _opened;
    private int _opens;
    private int _closes;
    private int _disconnectError;

    internal string? Model { get; init; } = " Canon Digital Camera ";
    internal string Maker { get; init; } = " Canon ";
    internal uint Battery { get; init; } = 72;
    internal bool ReadOnly { get; init; }
    internal bool MissingDetails { get; init; }
    internal Action? BeforeOpen { get; init; }

    internal CameraReading Read() => Read(camera);

    internal CameraReading Read(CameraDevice discovered)
    {
        try { return WindowsCamera.Read(discovered, this); }
        finally
        {
            if (BeforeOpen is null)
            {
                Require(_opens == (ReadOnly ? 2 : 1) && _closes == 1 && !_opened, "one successful open and close");
                Require(_remaining.Count == 0 && _active is null, "all commands and transfers completed");
            }
        }
    }

    public void Open(string id, IPortableDeviceValues client)
    {
        Require(!_opened && id == camera.Id, "device ID passed to Open");
        Require(client.GetStringValue(new(Client, 2), out var name) >= 0 && name == "Camera Inspector", "WPD client name");
        int status = client.GetUnsignedIntegerValue(new(Client, 9), out uint access);
        if (++_opens == 1)
        {
            Require(status < 0, "first open uses default access");
            BeforeOpen?.Invoke();
            if (ReadOnly)
                throw new COMException("Emulated camera requires read-only access.", AccessDenied);
        }
        else
            Require(ReadOnly && _opens == 2 && status >= 0 && access == 0x80000000, "retry uses GENERIC_READ");
        _opened = true;
    }

    public int Close()
    {
        Require(_opened, "closing an open camera");
        _opened = false;
        _closes++;
        return 0;
    }

    public void Content(out IPortableDeviceContent content)
    {
        Require(_opened, "metadata requires an open camera");
        if (MissingDetails)
            throw new COMException("Emulated camera has no metadata.");
        content = this;
    }

    public void Properties(out IPortableDeviceProperties properties) => properties = this;

    public void GetValues(string objectId, IPortableDeviceKeyCollection keys, out IPortableDeviceValues values)
    {
        Require(objectId == "DEVICE", "metadata object ID");
        values = NewValues();
        if (Model is not null)
            values.SetStringValue(new(Device, 8), Model);
        values.SetStringValue(new(Device, 7), Maker);
        values.SetStringValue(new(Device, 9), " EMULATED-123 ");
        values.SetStringValue(new(Device, 3), " 1.3.6 ");
        values.SetUnsignedIntegerValue(new(Device, 4), Battery);
    }

    public void SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues results)
    {
        Require(_opened && !ReadOnly && flags == 0, "commands require an open writable camera");
        parameters.GetGuidValue(new(Common, 1001), out Guid category);
        int status = parameters.GetUnsignedIntegerValue(new(Common, 1002), out uint command);
        Require(category == Mtp && status >= 0, "WPD MTP command header");

        results = NewValues();
        try
        {
            results.SetErrorValue(new(Common, 1003), _disconnectError);
            if (_disconnectError != 0 && command != 17)
                return;

            if (command is 12 or 13 or 14)
                Begin(command, parameters, results);
            else
                Continue(command, parameters, results);
        }
        catch
        {
            Marshal.ReleaseComObject(results);
            throw;
        }
    }

    private void Begin(uint command, IPortableDeviceValues input, IPortableDeviceValues output)
    {
        Require(_active is null, "previous transfer ended");
        Require(input.GetUnsignedIntegerValue(new(Mtp, 1001), out uint opcode) >= 0, "PTP operation code");
        if (!_remaining.TryDequeue(out var next))
            throw new InvalidOperationException($"{camera.Id}: unexpected PTP operation 0x{opcode:X4}.");
        Require(command == next.Command && opcode == next.Opcode, $"expected WPD {next.Command}, PTP 0x{next.Opcode:X4}, got WPD {command}, PTP 0x{opcode:X4}");

        input.GetIPortableDevicePropVariantCollectionValue(new(Mtp, 1002), out var collection);
        using var arguments = new ComLease<IPortableDevicePropVariantCollection>(collection);
        arguments.Value.GetCount(out uint count);
        Require(count == next.Arguments.Length, $"argument count for 0x{opcode:X4}");
        for (uint i = 0; i < count; i++)
        {
            arguments.Value.GetAt(i, out var argument);
            Require(argument.Type == 19 && argument.Value == next.Arguments[i], $"argument {i} for 0x{opcode:X4}");
        }

        if (command == 12)
        {
            output.SetUnsignedIntegerValue(new(Mtp, 1003), next.Response);
            return;
        }

        if (command == 14)
            Require(input.GetUnsignedLargeIntegerValue(new(Mtp, 1007), out ulong size) >= 0 && size == (ulong)next.Data.Length, "announced write size");
        else
            output.SetUnsignedLargeIntegerValue(new(Mtp, 1007), next.AnnouncedSize ?? (ulong)next.Data.Length);

        _active = next;
        _offset = 0;
        _readCalls = 0;
        _context = $"transfer-{_remaining.Count}";
        output.SetStringValue(new(Mtp, 1006), _context);
    }

    private void Continue(uint command, IPortableDeviceValues input, IPortableDeviceValues output)
    {
        var active = _active ?? throw new InvalidOperationException($"{camera.Id}: transfer command {command} without a transfer.");
        Require(input.GetStringValue(new(Mtp, 1006), out var context) >= 0 && context == _context, "transfer context round trip");
        switch (command)
        {
            case 15 when active.Command == 13:
                _readCalls++;
                Require(input.GetUnsignedIntegerValue(new(Mtp, 1008), out uint ask) >= 0 && ask is > 0 and <= 16_384, "requested read size");
                if (active.ReadError != 0 && _offset >= active.ReadErrorAfterBytes)
                {
                    if (active.DisconnectOnReadError is { } disconnect)
                    {
                        _disconnectError = active.ReadError;
                        disconnect();
                    }
                    output.SetErrorValue(new(Common, 1003), active.ReadError);
                    return;
                }
                int length = Math.Min((int)ask, Math.Min(active.ChunkSize, active.Data.Length - _offset));
                output.SetUnsignedIntegerValue(new(Mtp, 1009), (uint)length);
                if (length > 0)
                    output.SetBufferValue(new(Mtp, 1012), active.Data.AsSpan(_offset, length).ToArray(), (uint)length);
                _offset += length;
                break;
            case 16 when active.Command == 14:
                Require(_offset == 0 && input.GetUnsignedIntegerValue(new(Mtp, 1010), out uint size) >= 0 && size == active.Data.Length, "requested write size");
                input.GetBufferValue(new(Mtp, 1012), out var buffer, out uint available);
                try
                {
                    Require(buffer != IntPtr.Zero && available == active.Data.Length, "write buffer size");
                    var data = new byte[available];
                    Marshal.Copy(buffer, data, 0, data.Length);
                    Require(data.AsSpan().SequenceEqual(active.Data), "Canon monitor command payload");
                }
                finally
                {
                    Marshal.FreeCoTaskMem(buffer);
                }
                _offset = Math.Min(active.ChunkSize, active.Data.Length);
                output.SetUnsignedIntegerValue(new(Mtp, 1011), (uint)_offset);
                break;
            case 17:
                Require(active.ExpectedReadCalls is null || active.ExpectedReadCalls == _readCalls,
                    $"expected {active.ExpectedReadCalls} data reads for 0x{active.Opcode:X4}, got {_readCalls}");
                if (active.EndError != 0)
                    output.SetErrorValue(new(Common, 1003), active.EndError);
                output.SetUnsignedIntegerValue(new(Mtp, 1003), active.Response);
                _active = null;
                break;
            default:
                throw new InvalidOperationException($"{camera.Id}: unexpected WPD transfer command {command}.");
        }
    }

    public void Capabilities() => throw new NotSupportedException();
    public void Cancel() => throw new NotSupportedException();
    public void EnumObjects() => throw new NotSupportedException();
    public void GetSupportedProperties() => throw new NotSupportedException();
    public void GetPropertyAttributes() => throw new NotSupportedException();

    private static IPortableDeviceValues NewValues() =>
        (IPortableDeviceValues)Activator.CreateInstance(Type.GetTypeFromCLSID(ValuesClass, throwOnError: true)!)!;

    private void Require(bool condition, string expected)
    {
        if (!condition)
            throw new InvalidOperationException($"{camera.Id}: {expected}.");
    }
}

internal sealed record CameraExchange(uint Command, uint Opcode, uint[] Arguments, byte[] Data)
{
    internal uint Response { get; init; } = 0x2001;
    internal ulong? AnnouncedSize { get; init; }
    internal int ChunkSize { get; init; } = int.MaxValue;
    internal int ReadError { get; init; }
    internal int ReadErrorAfterBytes { get; init; }
    internal int EndError { get; init; }
    internal Action? DisconnectOnReadError { get; init; }
    internal int? ExpectedReadCalls { get; init; }
}
