using System.Runtime.InteropServices;

namespace OpenM50Webcam.Wpd;

internal sealed record WpdDeviceInfo(string Id, string Name, string Manufacturer, string Description);

internal readonly record struct MtpResponse(ushort Code)
{
    public bool Ok => Code == 0x2001;
    public override string ToString() => $"0x{Code:X4}";
}

/// <summary>
/// Sends raw PTP/MTP operations (including vendor operations) to a device through the
/// stock Windows WPD driver, using the WPD "MTP extension" commands. No driver swap needed.
/// All calls must happen on the thread that opened the device (MTA).
/// </summary>
internal sealed class MtpDevice : IDisposable
{
    private const int MaxChunk = 8 * 1024 * 1024;

    private readonly IPortableDevice _device;
    private IntPtr _scratch; // zeroed buffer passed as the (ignored) input of READ_DATA
    private int _scratchSize;

    public WpdDeviceInfo Info { get; }

    private MtpDevice(IPortableDevice device, WpdDeviceInfo info)
    {
        _device = device;
        Info = info;
    }

    // ---------------------------------------------------------------- enumeration

    public static List<WpdDeviceInfo> Enumerate()
    {
        var manager = (IPortableDeviceManager)CreateCom(WpdClsid.PortableDeviceManager);
        try
        {
            manager.RefreshDeviceList();
            uint count = 0;
            manager.GetDevices(IntPtr.Zero, ref count);
            var result = new List<WpdDeviceInfo>();
            if (count == 0) return result;

            IntPtr array = Marshal.AllocHGlobal((int)count * IntPtr.Size);
            try
            {
                manager.GetDevices(array, ref count);
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                    string id = Marshal.PtrToStringUni(p) ?? "";
                    Marshal.FreeCoTaskMem(p);
                    result.Add(new WpdDeviceInfo(
                        id,
                        ReadString(manager.GetDeviceFriendlyName, id),
                        ReadString(manager.GetDeviceManufacturer, id),
                        ReadString(manager.GetDeviceDescription, id)));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(array);
            }
            return result;
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    private delegate void StringGetter(string id, IntPtr buffer, ref uint length);

    private static string ReadString(StringGetter getter, string id)
    {
        const uint chars = 512;
        IntPtr buf = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            uint len = chars;
            getter(id, buf, ref len);
            return Marshal.PtrToStringUni(buf) ?? "";
        }
        catch (COMException)
        {
            return "";
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public static MtpDevice Open(WpdDeviceInfo info)
    {
        var device = (IPortableDevice)CreateCom(WpdClsid.PortableDeviceFTM);
        var client = NewValues();
        SetString(client, WpdKeys.ClientName, "OpenM50 Webcam");
        SetUInt(client, WpdKeys.ClientMajorVersion, 0);
        SetUInt(client, WpdKeys.ClientMinorVersion, 1);
        SetUInt(client, WpdKeys.ClientRevision, 0);
        device.Open(info.Id, client);
        Marshal.ReleaseComObject(client);
        return new MtpDevice(device, info);
    }

    // ---------------------------------------------------------------- operations

    public HashSet<ushort> GetSupportedVendorOpcodes()
    {
        var p = NewCommand(WpdKeys.CmdGetSupportedVendorOpcodes);
        var r = Send(p);
        var set = new HashSet<ushort>();
        var key = WpdKeys.VendorOperationCodes;
        r.GetIPortableDevicePropVariantCollectionValue(ref key, out var coll);
        uint n = 0;
        coll.GetCount(ref n);
        for (uint i = 0; i < n; i++)
        {
            coll.GetAt(i, out var v);
            set.Add((ushort)v.UIntVal);
        }
        Release(coll, r, p);
        return set;
    }

    public MtpResponse Execute(ushort opcode, params uint[] args)
    {
        var p = NewCommand(WpdKeys.CmdExecuteWithoutData);
        SetOperation(p, opcode, args);
        var r = Send(p);
        var code = GetUInt(r, WpdKeys.ResponseCode);
        Release(r, p);
        return new MtpResponse((ushort)code);
    }

    public MtpResponse ExecuteRead(ushort opcode, out byte[] data, params uint[] args)
    {
        var p = NewCommand(WpdKeys.CmdExecuteWithDataToRead);
        SetOperation(p, opcode, args);
        var r = Send(p);
        string context = GetString(r, WpdKeys.TransferContext);
        ulong total = GetULong(r, WpdKeys.TransferTotalDataSize);
        Release(r, p);

        try
        {
            data = total switch
            {
                0 => Array.Empty<byte>(),
                < int.MaxValue => ReadKnownLength(context, (int)total),
                _ => ReadUnknownLength(context),
            };
        }
        catch
        {
            TryEndTransfer(context);
            throw;
        }

        return EndTransfer(context);
    }

    public MtpResponse ExecuteWrite(ushort opcode, byte[] data, params uint[] args)
    {
        var p = NewCommand(WpdKeys.CmdExecuteWithDataToWrite);
        SetOperation(p, opcode, args);
        SetULong(p, WpdKeys.TransferTotalDataSize, (ulong)data.Length);
        var r = Send(p);
        string context = GetString(r, WpdKeys.TransferContext);
        Release(r, p);

        try
        {
            var w = NewCommand(WpdKeys.CmdWriteData);
            SetString(w, WpdKeys.TransferContext, context);
            SetUInt(w, WpdKeys.TransferNumBytesToWrite, (uint)data.Length);
            unsafe
            {
                fixed (byte* ptr = data)
                {
                    var key = WpdKeys.TransferData;
                    w.SetBufferValue(ref key, (IntPtr)ptr, (uint)data.Length);
                }
            }
            var wr = Send(w);
            Release(wr, w);
        }
        catch
        {
            TryEndTransfer(context);
            throw;
        }

        return EndTransfer(context);
    }

    private byte[] ReadKnownLength(string context, int total)
    {
        var data = new byte[total];
        int offset = 0;
        while (offset < total)
        {
            int n = ReadChunk(context, Math.Min(total - offset, MaxChunk), data, offset);
            if (n <= 0) break;
            offset += n;
        }
        if (offset != total) Array.Resize(ref data, offset);
        return data;
    }

    private byte[] ReadUnknownLength(string context)
    {
        using var ms = new MemoryStream();
        var chunk = new byte[1024 * 1024];
        while (true)
        {
            int n = ReadChunk(context, chunk.Length, chunk, 0);
            if (n <= 0) break;
            ms.Write(chunk, 0, n);
            if (n < chunk.Length) break;
        }
        return ms.ToArray();
    }

    private int ReadChunk(string context, int size, byte[] target, int targetOffset)
    {
        EnsureScratch(size);
        var p = NewCommand(WpdKeys.CmdReadData);
        SetString(p, WpdKeys.TransferContext, context);
        SetUInt(p, WpdKeys.TransferNumBytesToRead, (uint)size);
        var dataKey = WpdKeys.TransferData;
        p.SetBufferValue(ref dataKey, _scratch, (uint)size);
        var r = Send(p);

        int read = (int)GetUInt(r, WpdKeys.TransferNumBytesRead);
        r.GetBufferValue(ref dataKey, out IntPtr buf, out uint bufSize);
        try
        {
            int n = Math.Min(read, (int)bufSize);
            n = Math.Min(n, target.Length - targetOffset);
            if (n > 0) Marshal.Copy(buf, target, targetOffset, n);
            return n;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeCoTaskMem(buf);
            Release(r, p);
        }
    }

    private MtpResponse EndTransfer(string context)
    {
        var p = NewCommand(WpdKeys.CmdEndDataTransfer);
        SetString(p, WpdKeys.TransferContext, context);
        var r = Send(p);
        var code = GetUInt(r, WpdKeys.ResponseCode);
        Release(r, p);
        return new MtpResponse((ushort)code);
    }

    private void TryEndTransfer(string context)
    {
        try { EndTransfer(context); } catch { /* best effort */ }
    }

    private void EnsureScratch(int size)
    {
        if (size <= _scratchSize) return;
        if (_scratch != IntPtr.Zero) Marshal.FreeHGlobal(_scratch);
        _scratch = Marshal.AllocHGlobal(size);
        unsafe { new Span<byte>((void*)_scratch, size).Clear(); }
        _scratchSize = size;
    }

    // ---------------------------------------------------------------- helpers

    private IPortableDeviceValues Send(IPortableDeviceValues parameters)
    {
        _device.SendCommand(0, parameters, out var results);
        var key = WpdKeys.HResult;
        int hr;
        try { results.GetErrorValue(ref key, out hr); }
        catch (COMException) { hr = 0; } // no HRESULT in results: treat as success
        if (hr < 0)
        {
            Marshal.ReleaseComObject(results);
            Marshal.ThrowExceptionForHR(hr);
        }
        return results;
    }

    private static IPortableDeviceValues NewCommand(uint commandId)
    {
        var v = NewValues();
        var key = WpdKeys.CommandCategory;
        var cat = WpdKeys.MtpExtCategory;
        v.SetGuidValue(ref key, ref cat);
        SetUInt(v, WpdKeys.CommandId, commandId);
        return v;
    }

    private static void SetOperation(IPortableDeviceValues v, ushort opcode, uint[] args)
    {
        SetUInt(v, WpdKeys.OperationCode, opcode);
        var coll = (IPortableDevicePropVariantCollection)CreateCom(WpdClsid.PortableDevicePropVariantCollection);
        foreach (var a in args)
        {
            var pv = PropVariant.FromUInt(a);
            coll.Add(ref pv);
        }
        var key = WpdKeys.OperationParams;
        v.SetIPortableDevicePropVariantCollectionValue(ref key, coll);
        Marshal.ReleaseComObject(coll);
    }

    private static IPortableDeviceValues NewValues() =>
        (IPortableDeviceValues)CreateCom(WpdClsid.PortableDeviceValues);

    private static object CreateCom(Guid clsid) =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;

    private static void SetUInt(IPortableDeviceValues v, PropertyKey key, uint value) => v.SetUnsignedIntegerValue(ref key, value);
    private static void SetULong(IPortableDeviceValues v, PropertyKey key, ulong value) => v.SetUnsignedLargeIntegerValue(ref key, value);
    private static void SetString(IPortableDeviceValues v, PropertyKey key, string value) => v.SetStringValue(ref key, value);

    private static uint GetUInt(IPortableDeviceValues v, PropertyKey key)
    {
        v.GetUnsignedIntegerValue(ref key, out uint value);
        return value;
    }

    private static ulong GetULong(IPortableDeviceValues v, PropertyKey key)
    {
        v.GetUnsignedLargeIntegerValue(ref key, out ulong value);
        return value;
    }

    private static string GetString(IPortableDeviceValues v, PropertyKey key)
    {
        v.GetStringValue(ref key, out string value);
        return value;
    }

    private static void Release(params object?[] objects)
    {
        foreach (var o in objects)
            if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    public void Dispose()
    {
        try { _device.Close(); } catch { /* ignore */ }
        Marshal.ReleaseComObject(_device);
        if (_scratch != IntPtr.Zero) Marshal.FreeHGlobal(_scratch);
        _scratch = IntPtr.Zero;
    }
}
