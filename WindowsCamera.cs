using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CameraInspector;

internal static partial class WindowsCamera
{
    private static readonly Guid ClientCategory = new("204d9f0c-2292-4080-9f42-40664e70f859");
    private static readonly Guid DeviceCategory = new("26d4979a-e643-4626-9e2b-736dc0c92fdc");
    private static readonly Guid CommonCategory = new("f0422a9c-5dc8-4440-b5bd-5df28835658a");
    private static readonly Guid MtpCategory = new("4d545058-1a2e-4106-a357-771e0819fc56");

    private const uint PtpOk = 0x2001;
    private const uint GetDevicePropValue = 0x1015;
    private const uint GetDeviceInfoEx = 0x9108;
    private const uint SetRemoteMode = 0x9114;
    private const uint SetEventMode = 0x9115;
    private const uint GetEvent = 0x9116;
    private const uint RequestProperty = 0x9127;
    private const uint SendMonitorCommand = 0x9052;
    private const uint ReadMonitorReply = 0x9053;
    private const int MaxTransferBytes = 1024 * 1024;
    private const int DeviceNotConnected = unchecked((int)0x8007048F);

    internal static List<CameraDevice> Find()
    {
        using var manager = ComLease<IPortableDeviceManager>.Create("0af10cec-2ecd-4b92-9581-34f6ae0637f3");
        return Find(manager.Value);
    }

    internal static unsafe List<CameraDevice> Find(IPortableDeviceManager manager)
    {
        manager.RefreshDeviceList();
        uint count = 0;
        manager.GetDevices(IntPtr.Zero, ref count);
        if (count == 0)
            return [];

        var ids = new IntPtr[count];
        try
        {
            fixed (IntPtr* buffer = ids)
                manager.GetDevices((IntPtr)buffer, ref count);

            var cameras = new List<CameraDevice>();
            foreach (var pointer in ids)
            {
                var id = Marshal.PtrToStringUni(pointer);
                if (string.IsNullOrEmpty(id))
                    continue;

                var name = FriendlyName(manager, id);
                if (NikonDslr.ConnectedModel(id, name) is { } nikon)
                    cameras.Add(new(id, nikon));
                else if (CameraIdentity.MakerForUsb(id, name) is not null || IsStillCamera(manager, id))
                    cameras.Add(new(id, name));
            }

            return cameras;
        }
        finally
        {
            foreach (var pointer in ids)
                Marshal.FreeCoTaskMem(pointer);
        }
    }

    internal static CameraReading Read(CameraDevice camera)
    {
        using var device = ComLease<IPortableDevice>.Create("f7c0039a-4762-488a-b4b3-760ef9a1ba9b");
        return Read(camera, device.Value);
    }

    internal static CameraReading Read(CameraDevice camera, IPortableDevice device)
    {
        using var client = NewValues();
        client.Value.SetStringValue(new(ClientCategory, 2), "Camera Inspector");

        bool allowCommands = true;
        try
        {
            device.Open(camera.Id, client.Value);
        }
        catch (Exception error) when (error is UnauthorizedAccessException ||
                                      error is COMException { HResult: unchecked((int)0x80070005) })
        {
            client.Value.SetUnsignedIntegerValue(new(ClientCategory, 9), 0x80000000); // GENERIC_READ
            device.Open(camera.Id, client.Value);
            allowCommands = false;
        }

        try
        {
            var nikonModel = NikonDslr.ConnectedModel(camera.Id, camera.Name);
            var maker = nikonModel is null ? CameraIdentity.MakerForUsb(camera.Id, camera.Name) : "Nikon";
            var reading = new CameraReading(nikonModel ?? camera.Name ?? CameraDevice.UnknownName,
                maker ?? "—", null, null, null, null);
            try { reading = ReadDetails(device, reading); }
            catch (COMException error) when (!IsDeviceDisconnected(error)) { Debug.WriteLine(error); }

            if (nikonModel is not null)
                reading = reading with { Model = nikonModel };
            else if (maker == "Nikon")
                reading = reading with { Model = NikonNef.ModelName(reading.Model) ?? NikonNef.ModelName(camera.Name) ?? reading.Model };

            var usbModel = maker == "Canon" ? CanonShutter.UsbModel(camera.Id) : null;
            if (usbModel is not null && CanonShutter.ModelKey(usbModel) != CanonShutter.ModelKey(reading.Model))
                reading = reading with { Model = usbModel };

            if (!allowCommands)
                return reading;

            string vendor = CameraIdentity.UsbVendor(camera.Id);
            if (maker == "Canon" && reading.Model.Contains("EOS", StringComparison.OrdinalIgnoreCase))
            {
                var (count, errorCode) = ReadEosData(device, CanonShutter.ProtocolFor(reading.Model), usbModel is not null);
                reading = reading with { Count = count, DeviceErrorCode = errorCode };
            }
            else
                reading = ReadVendorError(device, reading, vendor);

            return reading.StorageIssues is null
                ? reading with { StorageIssues = ReadPtpStorageIssues(device, vendor) }
                : reading;
        }
        catch (Exception error) when (IsDeviceDisconnected(error))
        {
            throw new CameraDisconnectedException(error);
        }
        finally
        {
            device.Close();
        }
    }

    private static bool IsStillCamera(IPortableDeviceManager manager, string id)
    {
        // The WPD class installer registers the device type as a REG_DWORD.
        uint size = sizeof(uint), value = 0;
        return manager.GetDeviceProperty(id, "PORTABLE_DEVICE_TYPE", ref value, ref size, out uint type) >= 0
            && type == 4 && size == sizeof(uint) && value == 1;
    }

    private static string? FriendlyName(IPortableDeviceManager manager, string id)
    {
        uint length = 0;
        manager.GetDeviceFriendlyName(id, null, ref length);
        if (length is 0 or > 4096)
            return null;

        var name = new StringBuilder((int)length);
        return manager.GetDeviceFriendlyName(id, name, ref length) >= 0
            ? Clean(name.ToString())
            : null;
    }

    private static CameraReading ReadDetails(IPortableDevice device, CameraReading defaults)
    {
        device.Content(out var rawContent);
        using var content = new ComLease<IPortableDeviceContent>(rawContent);
        content.Value.Properties(out var rawProperties);
        using var properties = new ComLease<IPortableDeviceProperties>(rawProperties);
        using var keys = ComLease<IPortableDeviceKeyCollection>.Create("de2d022d-2480-43be-97f0-d1fa2cf98f4f");
        foreach (uint id in new uint[] { 3, 4, 7, 8, 9 })
            keys.Value.Add(new(DeviceCategory, id));

        properties.Value.GetValues("DEVICE", keys.Value, out var rawValues);
        using var values = new ComLease<IPortableDeviceValues>(rawValues);
        var model = ReadString(values.Value, new(DeviceCategory, 8));
        if (defaults.Maker == "Canon" && model is not null && !model.Contains("EOS", StringComparison.OrdinalIgnoreCase)
            && defaults.Model.Contains("EOS", StringComparison.OrdinalIgnoreCase))
            model = defaults.Model;

        return new(model ?? defaults.Model, ReadString(values.Value, new(DeviceCategory, 7)) ?? defaults.Maker,
            ReadString(values.Value, new(DeviceCategory, 9)), ReadString(values.Value, new(DeviceCategory, 3)),
            values.Value.GetUnsignedIntegerValue(new(DeviceCategory, 4), out uint battery) >= 0 && battery <= 100 ? battery : null,
            null, StorageIssues: ReadWpdStorageIssues(content.Value, properties.Value));
    }

    private static (ShutterCount? Count, uint? DeviceErrorCode) ReadEosData(
        IPortableDevice device, CanonCounterProtocol protocol, bool identifiedByUsb)
    {
        if (protocol == CanonCounterProtocol.Monitor && identifiedByUsb && ReadMonitorShutter(device) is { } monitor)
            return (monitor, null);

        var eos = new CanonEosEvents(protocol) { Legacy = ReadLegacyCounter(device) };
        if (eos.Legacy.HasValue && protocol != CanonCounterProtocol.ReleaseEvent)
            return (eos.Result, null);

        bool remote = TryControl(device, SetRemoteMode, 1);
        bool events = TryControl(device, SetEventMode, 1);
        bool disconnected = false;
        try
        {
            if (events)
            {
                TryRead(device, GetDeviceInfoEx);
                ReadEvents(device, eos, 3);
                if (!eos.HasMechanical && protocol == CanonCounterProtocol.ReleaseEvent)
                    RequestAndRead(device, eos, CanonShutter.ReleaseProperty);
                if (!eos.HasMechanical && !eos.Legacy.HasValue)
                    RequestAndRead(device, eos, CanonShutter.LegacyProperty);
                if (!eos.HasMechanical && !eos.ScalarRelease.HasValue && protocol != CanonCounterProtocol.ReleaseEvent)
                    RequestAndRead(device, eos, CanonShutter.ReleaseProperty);
            }
        }
        catch (Exception error) when (IsDeviceDisconnected(error))
        {
            disconnected = true;
            throw;
        }
        finally
        {
            if (events && !disconnected)
                TryControl(device, SetEventMode, 0);
            if (remote && !disconnected)
                TryControl(device, SetRemoteMode, 0);
        }

        return (eos.Result, eos.DeviceErrorCode);
    }

    private static uint? ReadLegacyCounter(IPortableDevice device)
    {
        if (TryRead(device, GetDevicePropValue, CanonShutter.LegacyProperty) is not { Length: 4 } data)
            return null;
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return value == uint.MaxValue ? null : value;
    }

    private static void RequestAndRead(IPortableDevice device, CanonEosEvents eos, uint property)
    {
        if (TryControl(device, RequestProperty, property))
            ReadEvents(device, eos, 4);
    }

    private static void ReadEvents(IPortableDevice device, CanonEosEvents eos, int attempts)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(160);

            if (TryRead(device, GetEvent) is not { } data)
                return;

            eos.Parse(data);
            if (eos.HasMechanical)
                return;
        }
    }

    private static ShutterCount? ReadMonitorShutter(IPortableDevice device)
    {
        if (!TrySendMonitor(device, CanonShutter.MonitorSessionPayload(open: true), replyExpected: false))
            return null;

        bool disconnected = false;
        try
        {
            return TrySendMonitor(device, CanonShutter.MonitorShutterPayload(), replyExpected: true) &&
                TryRead(device, ReadMonitorReply, 0, 0, 1) is { } payload
                ? CanonShutter.DecodeMonitorShutter(payload)
                : null;
        }
        catch (Exception error) when (IsDeviceDisconnected(error))
        {
            disconnected = true;
            throw;
        }
        finally
        {
            if (!disconnected)
                TrySendMonitor(device, CanonShutter.MonitorSessionPayload(open: false), replyExpected: false);
        }
    }

    private static bool TrySendMonitor(IPortableDevice device, byte[] payload, bool replyExpected)
    {
        try
        {
            using var input = NewValues();
            AddOperation(input.Value, SendMonitorCommand, [0, replyExpected ? 1u : 0u]);
            input.Value.SetUnsignedLargeIntegerValue(new(MtpCategory, 1007), (ulong)payload.Length);
            using var begin = Send(device, 14, input.Value);
            if (ReadString(begin.Value, new(MtpCategory, 1006)) is not { } context)
                return false;

            using var transfer = NewValues();
            transfer.Value.SetStringValue(new(MtpCategory, 1006), context);
            uint length = (uint)payload.Length;
            uint? response;
            bool complete;
            bool disconnected = false;
            try
            {
                transfer.Value.SetUnsignedIntegerValue(new(MtpCategory, 1010), length);
                transfer.Value.SetBufferValue(new(MtpCategory, 1012), payload, length);
                using var sent = Send(device, 16, transfer.Value);
                complete = sent.Value.GetUnsignedIntegerValue(new(MtpCategory, 1011), out uint written) >= 0 && written == length;
            }
            catch (Exception error) when (IsDeviceDisconnected(error))
            {
                disconnected = true;
                throw;
            }
            finally { response = EndTransfer(device, transfer.Value, disconnected); }
            return complete && response == PtpOk;
        }
        catch (Exception error) when (error is (COMException or UnauthorizedAccessException) && !IsDeviceDisconnected(error))
        {
            Debug.WriteLine(error);
            return false;
        }
    }

    private static byte[]? TryRead(IPortableDevice device, uint opcode, params uint[] arguments) =>
        TryReadResponse(device, opcode, arguments).Data;

    private static (byte[]? Data, uint? Response) TryReadResponse(IPortableDevice device, uint opcode, params uint[] arguments)
    {
        try { return Transfer(device, opcode, arguments); }
        catch (Exception error) when (error is (COMException or UnauthorizedAccessException or IOException) && !IsDeviceDisconnected(error))
        {
            Debug.WriteLine(error);
            return default;
        }
    }

    private static bool TryControl(IPortableDevice device, uint opcode, uint argument)
    {
        try
        {
            using var input = NewValues();
            AddOperation(input.Value, opcode, [argument]);
            using var result = Send(device, 12, input.Value);
            return ReadResponse(result.Value) == PtpOk;
        }
        catch (Exception error) when (error is (COMException or UnauthorizedAccessException) && !IsDeviceDisconnected(error))
        {
            Debug.WriteLine(error);
            return false;
        }
    }

    private static (byte[]? Data, uint? Response) Transfer(IPortableDevice device, uint opcode, uint[] arguments)
    {
        using var input = NewValues();
        AddOperation(input.Value, opcode, arguments);
        using var begin = Send(device, 13, input.Value);
        var context = ReadString(begin.Value, new(MtpCategory, 1006));
        if (context is null)
            return (null, ReadResponse(begin.Value));

        using var transfer = NewValues();
        transfer.Value.SetStringValue(new(MtpCategory, 1006), context);
        byte[]? data = null;
        uint? response;
        bool disconnected = false;
        try
        {
            ulong? size = begin.Value.GetUnsignedLargeIntegerValue(new(MtpCategory, 1007), out ulong total) >= 0
                && total != uint.MaxValue ? total : null;
            if (size is null || size <= MaxTransferBytes)
                data = ReadChunks(device, transfer.Value, size);
        }
        catch (Exception error) when (IsDeviceDisconnected(error))
        {
            disconnected = true;
            throw;
        }
        finally { response = EndTransfer(device, transfer.Value, disconnected); }

        return (response == PtpOk ? data : null, response);
    }

    private static uint? EndTransfer(IPortableDevice device, IPortableDeviceValues transfer, bool disconnected)
    {
        try
        {
            using var end = Send(device, 17, transfer);
            return ReadResponse(end.Value);
        }
        catch (Exception error) when (disconnected && error is (COMException or UnauthorizedAccessException or IOException))
        {
            // A cleanup error can otherwise replace the original disconnect.
            Debug.WriteLine(error);
            return null;
        }
    }

    private static bool IsDeviceDisconnected(Exception error) => error.HResult == DeviceNotConnected;

    private static byte[]? ReadChunks(IPortableDevice device, IPortableDeviceValues transfer, ulong? size)
    {
        using var payload = new MemoryStream();
        var chunk = new byte[16 * 1024];
        var dataKey = new PropertyKey(MtpCategory, 1012);
        while (payload.Length < MaxTransferBytes && (size is null || (ulong)payload.Length < size))
        {
            uint ask = size.HasValue ? (uint)Math.Min((ulong)chunk.Length, size.Value - (ulong)payload.Length) : (uint)chunk.Length;
            transfer.SetUnsignedIntegerValue(new(MtpCategory, 1008), ask);
            transfer.SetBufferValue(dataKey, chunk, ask);
            using var read = Send(device, 15, transfer);
            if (read.Value.GetUnsignedIntegerValue(new(MtpCategory, 1009), out uint actual) < 0 || actual > ask)
                return null;
            if (actual == 0)
                return size is null ? payload.ToArray() : null;

            read.Value.GetBufferValue(dataKey, out var buffer, out uint available);
            try
            {
                if (buffer == IntPtr.Zero || available < actual)
                    return null;
                Marshal.Copy(buffer, chunk, 0, (int)actual);
                payload.Write(chunk, 0, (int)actual);
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }

            if (size is null && actual < ask)
                return payload.ToArray();
        }

        return size is ulong expected && (ulong)payload.Length == expected ? payload.ToArray() : null;
    }

    private static void AddOperation(IPortableDeviceValues input, uint opcode, uint[] arguments)
    {
        using var parameters = ComLease<IPortableDevicePropVariantCollection>.Create("08a99e2f-6d6d-4b80-af5a-baf2bcbe4cb9");
        foreach (uint value in arguments)
            parameters.Value.Add(new() { Type = 19, Value = value }); // VT_UI4

        input.SetUnsignedIntegerValue(new(MtpCategory, 1001), opcode);
        input.SetIPortableDevicePropVariantCollectionValue(new(MtpCategory, 1002), parameters.Value);
    }

    private static ComLease<IPortableDeviceValues> Send(IPortableDevice device, uint command, IPortableDeviceValues input)
    {
        input.SetGuidValue(new(CommonCategory, 1001), MtpCategory);
        input.SetUnsignedIntegerValue(new(CommonCategory, 1002), command);
        device.SendCommand(0, input, out var result);
        var lease = new ComLease<IPortableDeviceValues>(result);
        try
        {
            result.GetErrorValue(new(CommonCategory, 1003), out var status);
            if (status < 0)
                throw new COMException("The Windows portable device driver rejected the command.", status);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static uint? ReadResponse(IPortableDeviceValues result) =>
        result.GetUnsignedIntegerValue(new(MtpCategory, 1003), out var response) >= 0 ? response : null;

    private static ComLease<IPortableDeviceValues> NewValues() =>
        ComLease<IPortableDeviceValues>.Create("0c15d503-d017-47ce-9016-7b3f978721cc");

    private static string? ReadString(IPortableDeviceValues values, PropertyKey key) =>
        values.GetStringValue(key, out var value) >= 0 ? Clean(value) : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class CameraDisconnectedException(Exception innerException)
    : IOException("The camera disconnected while it was being read.", innerException);
