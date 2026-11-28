using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CameraInspector;

internal static partial class WindowsCamera
{
    private static readonly Guid StorageCategory = new("01a3057a-74d6-4e80-bea7-dc4c212ce50a");
    private static readonly PropertyKey StorageType = new(StorageCategory, 2);
    private static readonly PropertyKey StorageCapacity = new(StorageCategory, 4);
    private static readonly PropertyKey StorageFreeBytes = new(StorageCategory, 5);
    private static readonly PropertyKey StorageAccess = new(StorageCategory, 11);
    private const uint GetStorageIds = 0x1004;
    private const uint GetStorageInfo = 0x1005;
    private const uint SonyGetDevicePropertyValue = 0x9204;
    private const int MaxStorageObjects = 16;

    private static CameraReading ReadVendorError(IPortableDevice device, CameraReading reading, string vendor)
    {
        // Sony's vendor operation overlaps other manufacturers' commands. Route by the USB vendor ID.
        return vendor switch
        {
            "04b0" when TryRead(device, GetDevicePropValue, 0xD1B2) is [byte nikon] =>
                reading with { ErrorSource = CameraErrorSource.NikonDisplay, DeviceErrorCode = nikon },
            "04cb" when TryRead(device, GetDevicePropValue, 0xD21B) is { Length: 2 } fuji =>
                reading with { ErrorSource = CameraErrorSource.FujifilmDevice, DeviceErrorCode = BinaryPrimitives.ReadUInt16LittleEndian(fuji) },
            "054c" when reading.Maker.StartsWith("Sony", StringComparison.OrdinalIgnoreCase) &&
                TryRead(device, SonyGetDevicePropertyValue, 0xD251) is [byte sony] =>
                reading with { ErrorSource = CameraErrorSource.SonyTemperature, DeviceErrorCode = sony },
            _ => reading
        };
    }

    private static CameraStorageIssues? ReadWpdStorageIssues(IPortableDeviceContent content, IPortableDeviceProperties properties)
    {
        CameraStorageIssues? issues = null;
        try
        {
            content.EnumObjects(0, "DEVICE", null, out var rawEnumeration);
            using var enumeration = new ComLease<IEnumPortableDeviceObjectIDs>(rawEnumeration);
            using var keys = ComLease<IPortableDeviceKeyCollection>.Create("de2d022d-2480-43be-97f0-d1fa2cf98f4f");
            foreach (var key in new[] { StorageType, StorageCapacity, StorageFreeBytes, StorageAccess })
                keys.Value.Add(key);

            for (int i = 0; i < MaxStorageObjects; i++)
            {
                IntPtr pointer = IntPtr.Zero;
                try
                {
                    enumeration.Value.Next(1, out pointer, out uint fetched);
                    if (fetched == 0)
                        break;
                    if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } id &&
                        ReadWpdStorageObject(properties, keys.Value, id) is { } item)
                        issues = issues.GetValueOrDefault() | item;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
        catch (Exception error) when (error is (COMException or UnauthorizedAccessException) && !IsDeviceDisconnected(error))
        {
            Debug.WriteLine(error);
        }
        return issues;
    }

    private static CameraStorageIssues? ReadWpdStorageObject(
        IPortableDeviceProperties properties, IPortableDeviceKeyCollection keys, string id)
    {
        try
        {
            properties.GetValues(id, keys, out var rawValues);
            using var values = new ComLease<IPortableDeviceValues>(rawValues);
            return ClassifyStorage(
                values.Value.GetUnsignedIntegerValue(StorageType, out uint type) >= 0 ? type : null,
                values.Value.GetUnsignedIntegerValue(StorageAccess, out uint access) >= 0 ? access : null,
                values.Value.GetUnsignedLargeIntegerValue(StorageCapacity, out ulong capacity) >= 0 ? capacity : null,
                values.Value.GetUnsignedLargeIntegerValue(StorageFreeBytes, out ulong free) >= 0 ? free : null);
        }
        catch (Exception error) when (error is (COMException or UnauthorizedAccessException) && !IsDeviceDisconnected(error))
        {
            Debug.WriteLine(error);
            return null;
        }
    }

    private static CameraStorageIssues? ReadPtpStorageIssues(IPortableDevice device, string vendor)
    {
        var (ids, response) = TryReadResponse(device, GetStorageIds);
        if (ids is not { Length: >= 4 })
            return DecodeStorageResponse(response, vendor);

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(ids);
        if (count > MaxStorageObjects || ids.Length != 4 + count * 4)
            return null;

        CameraStorageIssues? issues = null;
        for (int offset = 4; offset < ids.Length; offset += 4)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(ids.AsSpan(offset));
            // A physical storage ID with no logical store does not establish that a card is absent.
            if ((id & 0xFFFF) == 0 || id == uint.MaxValue)
                continue;

            var (info, infoResponse) = TryReadResponse(device, GetStorageInfo, id);
            var item = info is { Length: >= 28 }
                ? ClassifyStorage(BinaryPrimitives.ReadUInt16LittleEndian(info),
                    BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(4)),
                    BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(6)),
                    BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(14)))
                : DecodeStorageResponse(infoResponse, vendor);
            if (item.HasValue)
                issues = issues.GetValueOrDefault() | item.Value;
        }
        return issues;
    }

    private static CameraStorageIssues? ClassifyStorage(uint? type, uint? access, ulong? capacity, ulong? free)
    {
        if (type is 1 or 2 or > 4)
            return null; // A ROM store is read-only by design.

        bool hasSpaceInfo = capacity is > 0 and < ulong.MaxValue && free <= capacity;
        CameraStorageIssues issues = access is 1 or 2 ? CameraStorageIssues.ReadOnly : CameraStorageIssues.None;
        if (hasSpaceInfo && free == 0)
            issues |= CameraStorageIssues.Full;

        return hasSpaceInfo || access is <= 2 ? issues : null;
    }

    private static CameraStorageIssues? DecodeStorageResponse(uint? response, string vendor) => response switch
    {
        0x200C => CameraStorageIssues.Full,
        0x200E => CameraStorageIssues.ReadOnly,
        0x2013 => CameraStorageIssues.Unavailable,
        0x2019 => CameraStorageIssues.Busy,
        0x200F => CameraStorageIssues.AccessError,
        0xA021 when vendor == "04b0" => CameraStorageIssues.AccessError,
        0xA022 when vendor == "04b0" => CameraStorageIssues.Unformatted,
        _ => null
    };
}
