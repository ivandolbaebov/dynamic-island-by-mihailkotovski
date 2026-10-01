using System.Runtime.InteropServices;

namespace DynamicIsland;

/// <summary>Master volume + output peak meter of the default playback device (Core Audio).</summary>
sealed class AudioService
{
    const int ERender = 0, EMultimedia = 1, ClsCtxAll = 23;
    const int VtLpwstr = 31, VtUi4 = 19, VtClsid = 72;
    const uint Headphones = 3, Headset = 5; // EndpointFormFactor
    static readonly TimeSpan Refresh = TimeSpan.FromSeconds(1);

    static readonly Guid DeviceFormat = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    static readonly PropertyKey DeviceDesc = new(DeviceFormat, 2);       // "Наушники"
    static readonly PropertyKey FriendlyName = new(DeviceFormat, 14);    // "Наушники (WH-1000XM4)"
    static readonly PropertyKey InterfaceName = new(new("026e516e-b814-414b-83cd-856d6fef4822"), 2); // "WH-1000XM4"
    static readonly PropertyKey FormFactor = new(new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
    static readonly PropertyKey ContainerId = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    /// <summary>What a playback device is and what it is called.</summary>
    /// <param name="Container">Shared by every part of one physical device: leads from the endpoint to its Bluetooth side.</param>
    public readonly record struct Output(string Kind, string Name, bool Headphones, Guid Container);

    /// <summary>The default playback device, or null while there is none.</summary>
    public Output? Device { get; private set; }

    IAudioEndpointVolume? _volume;
    IAudioMeterInformation? _meter;
    DateTime _checked = DateTime.MinValue;
    string? _deviceId;
    bool _seen;
    Output? _switched;

    /// <summary>The default device was swapped since the last call (headphones plugged in, Bluetooth connected...).</summary>
    public bool TakeSwitch(out Output device)
    {
        device = _switched ?? default;
        bool switched = _switched != null;
        _switched = null;
        return switched;
    }

    public bool TryGetVolume(out float level, out bool muted)
    {
        level = 0;
        muted = false;
        Ensure();
        if (_volume == null) return false;
        if (_volume.GetMasterVolumeLevelScalar(out level) != 0 || _volume.GetMute(out muted) != 0)
        {
            Release();
            return false;
        }
        return true;
    }

    /// <summary>Current output peak 0..1, or -1 when the meter is unavailable.</summary>
    public float Peak()
    {
        if (_meter == null || _meter.GetPeakValue(out float peak) != 0) return -1;
        return peak;
    }

    public void Nudge(float delta)
    {
        Ensure();
        if (_volume == null || _volume.GetMasterVolumeLevelScalar(out float level) != 0) return;
        Guid ctx = Guid.Empty;
        _volume.SetMasterVolumeLevelScalar(Math.Clamp(level + delta, 0f, 1f), ref ctx);
    }

    // The default device can change (headphones plugged in), so check which one it is every second
    // and re-bind when it is a different one.
    void Ensure()
    {
        if (DateTime.UtcNow - _checked < Refresh) return;
        _checked = DateTime.UtcNow;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice? device = null;
            try
            {
                string? id = null;
                if (enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out device) != 0 || device == null
                    || device.GetId(out id) != 0)
                    id = null;

                if (id != null && (id != _deviceId || _volume == null))
                {
                    Release();
                    Guid iid = typeof(IAudioEndpointVolume).GUID;
                    if (device!.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object? vol) == 0)
                        _volume = vol as IAudioEndpointVolume;

                    iid = typeof(IAudioMeterInformation).GUID;
                    if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object? meter) == 0)
                        _meter = meter as IAudioMeterInformation;

                    Device = Describe(device);
                    // the device the island starts with is not news
                    if (_seen && id != _deviceId) _switched = Device;
                }
                else if (id == null)
                {
                    Release();
                    Device = null;
                }
                _deviceId = id;
                _seen = true;
            }
            finally
            {
                if (device != null) Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    static Output Describe(IMMDevice device)
    {
        string kind = "", name = "", full = "";
        uint form = 0;
        Guid container = Guid.Empty;
        if (device.OpenPropertyStore(0, out IPropertyStore? store) == 0 && store != null)
        {
            kind = Text(store, DeviceDesc);
            name = Text(store, InterfaceName);
            full = Text(store, FriendlyName);
            form = Number(store, FormFactor);
            container = Id(store, ContainerId);
            Marshal.ReleaseComObject(store);
        }
        if (kind.Length == 0) kind = full;
        return new Output(kind, name, form is Headphones or Headset, container);
    }

    static string Text(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out PropVariant value) != 0) return "";
        try { return value.Type == VtLpwstr ? Marshal.PtrToStringUni(value.Value) ?? "" : ""; }
        finally { PropVariantClear(ref value); }
    }

    static uint Number(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out PropVariant value) != 0) return 0;
        try { return value.Type == VtUi4 ? (uint)(value.Value.ToInt64() & 0xFFFFFFFF) : 0; }
        finally { PropVariantClear(ref value); }
    }

    static Guid Id(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out PropVariant value) != 0) return Guid.Empty;
        try { return value.Type == VtClsid && value.Value != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(value.Value) : Guid.Empty; }
        finally { PropVariantClear(ref value); }
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant value);

    void Release()
    {
        if (_volume != null) Marshal.ReleaseComObject(_volume);
        if (_meter != null) Marshal.ReleaseComObject(_meter);
        _volume = null;
        _meter = null;
    }
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorCom { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore? properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
}

[StructLayout(LayoutKind.Sequential)]
record struct PropertyKey(Guid Format, uint Id);

/// <summary>PROPVARIANT: the type tag, then a union that is two pointers wide.</summary>
[StructLayout(LayoutKind.Sequential)]
struct PropVariant
{
    public ushort Type;
    ushort _reserved1, _reserved2, _reserved3;
    public IntPtr Value;
    IntPtr _rest;
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
}
