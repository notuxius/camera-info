using System.Runtime.InteropServices;
using System.Text;

namespace CameraInspector;

// Method order and types follow PortableDeviceApi.h and PortableDeviceTypes.h.
// Unused slots are declared only to preserve the IUnknown vtable layout.
[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKey(Guid category, uint id)
{
    public readonly Guid Category = category;
    public readonly uint Id = id;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct UIntPropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public uint Value;
}

[ComImport, Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceManager
{
    void GetDevices(IntPtr ids, ref uint count);
    void RefreshDeviceList();
    [PreserveSig] int GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string id,
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder? name, ref uint length);
    void GetDeviceDescription();
    void GetDeviceManufacturer();
    [PreserveSig] int GetDeviceProperty([MarshalAs(UnmanagedType.LPWStr)] string id,
        [MarshalAs(UnmanagedType.LPWStr)] string property, ref uint value, ref uint size, out uint type);
}

[ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevice
{
    void Open([MarshalAs(UnmanagedType.LPWStr)] string id, IPortableDeviceValues client);
    void SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues results);
    void Content(out IPortableDeviceContent content);
    void Capabilities();
    void Cancel();
    [PreserveSig] int Close();
}

[ComImport, Guid("6a96ed84-7c73-4480-9938-bf5af477d426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceContent
{
    void EnumObjects();
    void Properties(out IPortableDeviceProperties properties);
}

[ComImport, Guid("7f6d695c-03df-4439-a809-59266beee3a6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceProperties
{
    void GetSupportedProperties();
    void GetPropertyAttributes();
    void GetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceKeyCollection keys,
        out IPortableDeviceValues values);
}

[ComImport, Guid("dada2357-e0ad-492e-98db-dd61c53ba353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceKeyCollection
{
    void GetCount();
    void GetAt();
    void Add(in PropertyKey key);
}

[ComImport, Guid("89b2e422-4f1b-4316-bcef-a44afea83eb3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevicePropVariantCollection
{
    void GetCount(out uint count);
    void GetAt(uint index, out UIntPropVariant value);
    void Add(in UIntPropVariant value);
}

[ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceValues
{
    void GetCount();
    void GetAt();
    void SetValue();
    void GetValue();
    void SetStringValue(in PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int GetStringValue(in PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
    void SetUnsignedIntegerValue(in PropertyKey key, uint value);
    [PreserveSig] int GetUnsignedIntegerValue(in PropertyKey key, out uint value);
    void SetSignedIntegerValue();
    void GetSignedIntegerValue();
    void SetUnsignedLargeIntegerValue(in PropertyKey key, ulong value);
    [PreserveSig] int GetUnsignedLargeIntegerValue(in PropertyKey key, out ulong value);
    void SetSignedLargeIntegerValue();
    void GetSignedLargeIntegerValue();
    void SetFloatValue();
    void GetFloatValue();
    void SetErrorValue(in PropertyKey key, int value);
    void GetErrorValue(in PropertyKey key, out int value);
    void SetKeyValue();
    void GetKeyValue();
    void SetBoolValue();
    void GetBoolValue();
    void SetIUnknownValue();
    void GetIUnknownValue();
    void SetGuidValue(in PropertyKey key, in Guid value);
    void GetGuidValue(in PropertyKey key, out Guid value);
    void SetBufferValue(in PropertyKey key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, uint size);
    void GetBufferValue(in PropertyKey key, out IntPtr buffer, out uint size);
    void SetIPortableDeviceValuesValue();
    void GetIPortableDeviceValuesValue();
    void SetIPortableDevicePropVariantCollectionValue(in PropertyKey key, IPortableDevicePropVariantCollection value);
    void GetIPortableDevicePropVariantCollectionValue(in PropertyKey key, out IPortableDevicePropVariantCollection value);
}

internal sealed class ComLease<T>(T value) : IDisposable where T : class
{
    internal T Value { get; } = value;

    internal static ComLease<T> Create(string clsid) =>
        new((T)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsid), throwOnError: true)!)!);

    public void Dispose()
    {
        if (Marshal.IsComObject(Value))
            Marshal.ReleaseComObject(Value);
    }
}
