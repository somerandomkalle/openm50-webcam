// Hand-written COM interop for the Windows Portable Devices (WPD) API.
// Only the parts needed to send raw MTP/PTP vendor commands are used, but every
// interface lists all its methods in vtable order so the slot layout is correct.
using System.Runtime.InteropServices;

namespace OpenM50Webcam.Wpd;

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FmtId;
    public uint Pid;

    public PropertyKey(Guid fmtId, uint pid)
    {
        FmtId = fmtId;
        Pid = pid;
    }
}

/// <summary>Minimal PROPVARIANT (x64 layout, 24 bytes). Only scalar values are used.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    public const ushort VT_UI4 = 19;

    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public uint UIntVal;
    [FieldOffset(8)] public IntPtr Ptr;

    public static PropVariant FromUInt(uint value) => new() { Vt = VT_UI4, UIntVal = value };
}

internal static class WpdClsid
{
    public static readonly Guid PortableDeviceManager = new("0AF10CEC-2ECD-4B92-9581-34F6AE0637F3");
    public static readonly Guid PortableDeviceFTM = new("F7C0039A-4762-488A-B4B3-760EF9A1BA9B");
    public static readonly Guid PortableDeviceValues = new("0C15D503-D017-47CE-9016-7B3F978721CC");
    public static readonly Guid PortableDevicePropVariantCollection = new("08A99E2F-6D6D-4B80-AF5A-BAF2BCBE4CB9");
}

internal static class WpdKeys
{
    // WPD_PROPERTY_COMMON_*
    private static readonly Guid Common = new("F0422A9C-5DC8-4440-B5BD-5DF28835658A");
    public static readonly PropertyKey CommandCategory = new(Common, 1001);
    public static readonly PropertyKey CommandId = new(Common, 1002);
    public static readonly PropertyKey HResult = new(Common, 1003);

    // WPD_CATEGORY_MTP_EXT_VENDOR_OPERATIONS and its properties
    public static readonly Guid MtpExtCategory = new("4D545058-1A2E-4106-A357-771E0819FC56");
    public const uint CmdGetSupportedVendorOpcodes = 11;
    public const uint CmdExecuteWithoutData = 12;
    public const uint CmdExecuteWithDataToRead = 13;
    public const uint CmdExecuteWithDataToWrite = 14;
    public const uint CmdReadData = 15;
    public const uint CmdWriteData = 16;
    public const uint CmdEndDataTransfer = 17;

    public static readonly PropertyKey OperationCode = new(MtpExtCategory, 1001);
    public static readonly PropertyKey OperationParams = new(MtpExtCategory, 1002);
    public static readonly PropertyKey ResponseCode = new(MtpExtCategory, 1003);
    public static readonly PropertyKey ResponseParams = new(MtpExtCategory, 1004);
    public static readonly PropertyKey VendorOperationCodes = new(MtpExtCategory, 1005);
    public static readonly PropertyKey TransferContext = new(MtpExtCategory, 1006);
    public static readonly PropertyKey TransferTotalDataSize = new(MtpExtCategory, 1007);
    public static readonly PropertyKey TransferNumBytesToRead = new(MtpExtCategory, 1008);
    public static readonly PropertyKey TransferNumBytesRead = new(MtpExtCategory, 1009);
    public static readonly PropertyKey TransferNumBytesToWrite = new(MtpExtCategory, 1010);
    public static readonly PropertyKey TransferNumBytesWritten = new(MtpExtCategory, 1011);
    public static readonly PropertyKey TransferData = new(MtpExtCategory, 1012);

    // WPD_CLIENT_INFORMATION_PROPERTIES_V1
    private static readonly Guid Client = new("204D9F0C-2292-4080-9F42-40664E70F859");
    public static readonly PropertyKey ClientName = new(Client, 2);
    public static readonly PropertyKey ClientMajorVersion = new(Client, 3);
    public static readonly PropertyKey ClientMinorVersion = new(Client, 4);
    public static readonly PropertyKey ClientRevision = new(Client, 5);
}

[ComImport, Guid("A1567595-4C2F-4574-A6FA-ECEF917B9A40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceManager
{
    void GetDevices(IntPtr pPnPDeviceIDs, ref uint pcPnPDeviceIDs);
    void RefreshDeviceList();
    void GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr buffer, ref uint length);
    void GetDeviceDescription([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr buffer, ref uint length);
    void GetDeviceManufacturer([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr buffer, ref uint length);
    void GetDeviceProperty(); // unused slot
    void GetPrivateDevices(); // unused slot
}

[ComImport, Guid("625E2DF8-6392-4CF0-9AD1-3CFA5F17775C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevice
{
    void Open([MarshalAs(UnmanagedType.LPWStr)] string pnpDeviceId, IPortableDeviceValues clientInfo);
    void SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues results);
    void Content(out IntPtr content);
    void Capabilities(out IntPtr capabilities);
    void Cancel();
    void Close();
    void Advise(); // unused slot
    void Unadvise(); // unused slot
    void GetPnPDeviceID(); // unused slot
}

[ComImport, Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceValues
{
    void GetCount(ref uint count);
    void GetAt(uint index, ref PropertyKey key, IntPtr value);
    void SetValue(ref PropertyKey key, ref PropVariant value);
    void GetValue(ref PropertyKey key, out PropVariant value);
    void SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    void GetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
    void SetUnsignedIntegerValue(ref PropertyKey key, uint value);
    void GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
    void SetSignedIntegerValue(ref PropertyKey key, int value);
    void GetSignedIntegerValue(ref PropertyKey key, out int value);
    void SetUnsignedLargeIntegerValue(ref PropertyKey key, ulong value);
    void GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong value);
    void SetSignedLargeIntegerValue(ref PropertyKey key, long value);
    void GetSignedLargeIntegerValue(ref PropertyKey key, out long value);
    void SetFloatValue(ref PropertyKey key, float value);
    void GetFloatValue(ref PropertyKey key, out float value);
    void SetErrorValue(ref PropertyKey key, int value);
    void GetErrorValue(ref PropertyKey key, out int value);
    void SetKeyValue(ref PropertyKey key, ref PropertyKey value);
    void GetKeyValue(ref PropertyKey key, out PropertyKey value);
    void SetBoolValue(ref PropertyKey key, int value);
    void GetBoolValue(ref PropertyKey key, out int value);
    void SetIUnknownValue(ref PropertyKey key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    void GetIUnknownValue(ref PropertyKey key, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    void SetGuidValue(ref PropertyKey key, ref Guid value);
    void GetGuidValue(ref PropertyKey key, out Guid value);
    void SetBufferValue(ref PropertyKey key, IntPtr buffer, uint size);
    void GetBufferValue(ref PropertyKey key, out IntPtr buffer, out uint size);
    void SetIPortableDeviceValuesValue(ref PropertyKey key, IPortableDeviceValues value);
    void GetIPortableDeviceValuesValue(ref PropertyKey key, out IPortableDeviceValues value);
    void SetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, IPortableDevicePropVariantCollection value);
    void GetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, out IPortableDevicePropVariantCollection value);
    void SetIPortableDeviceKeyCollectionValue(); // unused slot
    void GetIPortableDeviceKeyCollectionValue(); // unused slot
    void SetIPortableDeviceValuesCollectionValue(); // unused slot
    void GetIPortableDeviceValuesCollectionValue(); // unused slot
    void RemoveValue(ref PropertyKey key);
    void CopyValuesFromPropertyStore(IntPtr store);
    void CopyValuesToPropertyStore(IntPtr store);
    void Clear();
}

[ComImport, Guid("89B2E422-4F1B-4316-BCEF-A44AFEA83EB3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevicePropVariantCollection
{
    void GetCount(ref uint count);
    void GetAt(uint index, out PropVariant value);
    void Add(ref PropVariant value);
    void GetVarType(out ushort vt); // IDL name: GetType
    void ChangeType(ushort vt);
    void Clear();
    void RemoveAt(uint index);
}
