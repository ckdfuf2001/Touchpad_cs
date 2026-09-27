using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TouchPadCloneV2.Core;

/// <summary>
/// Touch input straight from Raw Input, the way the working reference on this
/// machine does it (TouchMousePointer.exe imports RegisterRawInputDevices /
/// GetRawInputData / GetRawInputDeviceInfoW).
///
/// Why it matters: input that arrives by HIT TESTING forces the pad's window to
/// stay hittable, which is exactly what makes our own synthetic clicks land on
/// the pad. Raw input with an input-sink target is delivered no matter which
/// window is under the finger, so the window can be made click-through and the
/// problem disappears at the root.
///
/// The digitizer is read from its HID reports: register for the Digitizer usage
/// page, then per report pull Contact Identifier / Tip Switch / X / Y out of
/// each contact's link collection using the HidP_* parser. Everything is logged
/// (caps, logical ranges, a sample of contacts) because the mapping from the
/// device's logical units to screen pixels varies per device and has to be
/// confirmed on real hardware.
/// </summary>
public sealed class RawTouchInput
{
    public Action<int, double, double, bool>? Contact; // id, screenX, screenY, isDown

    /// <summary>
    /// Screen rectangle of the pad, for a TOUCH PAD device (usage 0x05), whose
    /// reports are in its own 0..1 surface and not on the screen at all. A
    /// touch SCREEN (usage 0x04) reports absolute positions and needs no help.
    /// </summary>
    public Func<(double X, double Y, double W, double H)>? PadRectProvider;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_DEVNOTIFY = 0x00002000;
    private const int RIM_TYPEHID = 2;

    private const ushort HID_USAGE_PAGE_DIGITIZER = 0x0D;
    private const ushort USAGE_TOUCH_SCREEN = 0x04;
    private const ushort USAGE_TOUCH_PAD = 0x05;

    private const ushort USAGE_CONTACT_ID = 0x51;   // Contact Identifier
    private const ushort USAGE_TIP_SWITCH = 0x42;
    private const ushort USAGE_IN_RANGE = 0x32;
    private const ushort USAGE_X = 0x30;
    private const ushort USAGE_Y = 0x31;

    private const ushort HidP_Input = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage, usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType, dwSize;
        public IntPtr hDevice, wParam;
    }

    /// <summary>
    /// The first two fields of RAWHID. bRawData is NOT a pointer - the C
    /// declaration is "BYTE bRawData[1]", an inline array - so the report
    /// bytes start right after dwSizeHid and dwCount, at offset 8. Declaring
    /// it as IntPtr read the first eight bytes of the REPORT as an address and
    /// handed that to HidP_GetUsageValue, which is what crashed the app with
    /// an access violation (event log: 0xc0000005 in HidP_GetUsageValue).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWHID
    {
        public uint dwSizeHid, dwCount;
    }

    private const int RAWHID_DATA_OFFSET = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength,
            FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps,
            NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps,
            NumberOutputDataIndices, NumberFeatureButtonCaps,
            NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute,
            HasNull, Reserved;
        public ushort BitSize, ReportCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public ushort[] Reserved2;
        public uint UnitsExp, Units;
        public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        // The tail is a union; only the parts we read are declared.
        public ushort UsageMin, UsageMax;
        public ushort StringMin, StringMax, DesignatorMin, DesignatorMax,
            DataIndexMin, DataIndexMax;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command,
        IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint command,
        IntPtr data, ref uint size);
    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);
    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(ushort reportType,
        [Out] HIDP_VALUE_CAPS[] caps, ref ushort length, IntPtr preparsed);
    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(ushort reportType, ushort usagePage,
        ushort linkCollection, ushort usage, out uint value, IntPtr preparsed,
        IntPtr report, uint reportLength);

    private sealed class Device
    {
        public IntPtr Preparsed;
        public HIDP_CAPS Caps;
        public HIDP_VALUE_CAPS[] ValueCaps = Array.Empty<HIDP_VALUE_CAPS>();
        public ushort[] ContactLinks = Array.Empty<ushort>();
        public int Xmin, Xmax, Ymin, Ymax;
        public bool Logged;
    }

    private readonly Dictionary<IntPtr, Device> _devices = new();
    private readonly Dictionary<int, bool> _down = new();
    private bool _registered;
    private int _seen;

    /// <summary>Ask for the digitizer's reports, including when we are not focused.</summary>
    public bool Register(IntPtr hwnd)
    {
        if (_registered) return true;
        var list = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_DIGITIZER,
                usUsage = USAGE_TOUCH_SCREEN,
                dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
                hwndTarget = hwnd,
            },
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_DIGITIZER,
                usUsage = USAGE_TOUCH_PAD,
                dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
                hwndTarget = hwnd,
            },
        };
        _registered = RegisterRawInputDevices(list, (uint)list.Length,
            (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        DebugLog.Write($"RAWINPUT register touchscreen/touchpad = {_registered}"
            + $" (err {Marshal.GetLastWin32Error()})");
        return _registered;
    }

    /// <summary>Feed a WM_INPUT. Returns true if it was one of ours.</summary>
    public bool HandleMessage(IntPtr lParam)
    {
        try
        {
            _seen++;
            uint header = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            // Two calls, the standard way: ask how much room the report needs.
            // A fixed 88-byte buffer was too small for this device's HID report
            // (it wanted 106) and GetRawInputData returned -1 without setting
            // an error, which is exactly what the log showed as data=0xFFFFFFFF
            // with size=106 - the size it needed.
            uint need = 0;
            GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref need, header);
            if (need == 0) need = header + 512;
            IntPtr buf = Marshal.AllocHGlobal((int)need);
            try
            {
                uint size = need;
                uint got = GetRawInputData(lParam, RID_INPUT, buf, ref size, header);
                if (_seen <= 5 || (_seen % 200) == 0)
                {
                    DebugLog.Write($"RAWINPUT msg #{_seen} data={got}"
                        + $" need={need} size={size} err={Marshal.GetLastWin32Error()}");
                }
                if (got == uint.MaxValue || got == 0) return false;
                var rawHeader = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
                if (_seen <= 5)
                {
                    DebugLog.Write($"RAWINPUT type={rawHeader.dwType}"
                        + $" size={rawHeader.dwSize} dev={rawHeader.hDevice}");
                }
                if (rawHeader.dwType != RIM_TYPEHID) return false;
                var hid = Marshal.PtrToStructure<RAWHID>(
                    IntPtr.Add(buf, (int)header));
                if (hid.dwSizeHid == 0) return false;
                var device = Resolve(rawHeader.hDevice);
                if (device == null) return false;
                IntPtr data = IntPtr.Add(buf, (int)header + RAWHID_DATA_OFFSET);
                uint count = hid.dwCount == 0 ? 1 : hid.dwCount;
                for (uint i = 0; i < count; i++)
                {
                    int off = (int)header + RAWHID_DATA_OFFSET
                        + (int)(i * hid.dwSizeHid);
                    // Stay inside the buffer we were given: a native call with
                    // a pointer past the end is an access violation, and those
                    // cannot be caught in managed code - the app just dies.
                    if (off + (int)hid.dwSizeHid > (int)need) break;
                    IntPtr report = IntPtr.Add(buf, off);
                    Parse(device, report, hid.dwSizeHid);
                }
                return true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"RAWINPUT handle failed: {ex.GetType().Name}");
            return false;
        }
    }

    private Device? Resolve(IntPtr hDevice)
    {
        if (_devices.TryGetValue(hDevice, out var cached)) return cached;
        uint size = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return null;
        IntPtr prep = Marshal.AllocHGlobal((int)size);
        if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, prep, ref size) == 0)
        {
            Marshal.FreeHGlobal(prep);
            return null;
        }
        var dev = new Device { Preparsed = prep };
        if (HidP_GetCaps(prep, out dev.Caps) < 0) return null;
        ushort n = dev.Caps.NumberInputValueCaps;
        if (n > 0)
        {
            var caps = new HIDP_VALUE_CAPS[n];
            ushort len = n;
            if (HidP_GetValueCaps(HidP_Input, caps, ref len, prep) >= 0)
                dev.ValueCaps = caps;
        }
        // The contacts are the link collections that carry an X axis.
        var links = new List<ushort>();
        foreach (var vc in dev.ValueCaps)
        {
            if (vc.UsagePage != HID_USAGE_PAGE_DIGITIZER) continue;
            if (vc.UsageMin == USAGE_X || vc.UsageMax == USAGE_X)
            {
                if (vc.LogicalMax > vc.LogicalMin)
                {
                    dev.Xmin = vc.LogicalMin; dev.Xmax = vc.LogicalMax;
                }
            }
            if (vc.UsageMin == USAGE_Y || vc.UsageMax == USAGE_Y)
            {
                if (vc.LogicalMax > vc.LogicalMin)
                {
                    dev.Ymin = vc.LogicalMin; dev.Ymax = vc.LogicalMax;
                }
            }
            if (vc.UsageMin == USAGE_X && !links.Contains(vc.LinkCollection))
                links.Add(vc.LinkCollection);
        }
        dev.ContactLinks = links.ToArray();
        _devices[hDevice] = dev;
        return dev;
    }

    private void Parse(Device dev, IntPtr report, uint length)
    {
        int n = dev.ContactLinks.Length;
        if (n == 0)
        {
            // Some devices report a single contact with no link collections;
            // fall back to reading the usages globally.
            Emit(dev, 0, 0, report, length);
            return;
        }
        for (int k = 0; k < n; k++)
            Emit(dev, dev.ContactLinks[k], k, report, length);
    }

    private void Emit(Device dev, ushort link, int fallbackId,
        IntPtr report, uint length)
    {
        bool tip = Read(dev, link, USAGE_TIP_SWITCH, report, length) == 1;
        bool inRange = Read(dev, link, USAGE_IN_RANGE, report, length) == 1;
        uint idRaw = Read(dev, link, USAGE_CONTACT_ID, report, length);
        int id = (int)idRaw;
        if (id == 0 && idRaw == 0) id = fallbackId;
        uint rawX = Read(dev, link, USAGE_X, report, length);
        uint rawY = Read(dev, link, USAGE_Y, report, length);
        if (!dev.Logged)
        {
            dev.Logged = true;
            DebugLog.Write($"RAWINPUT device caps: usePage={dev.Caps.UsagePage:X}"
                + $" use={dev.Caps.Usage:X} links={dev.Caps.NumberLinkCollectionNodes}"
                + $" valCaps={dev.Caps.NumberInputValueCaps}"
                + $" reportBytes={dev.Caps.InputReportByteLength}"
                + $" X[{dev.Xmin}..{dev.Xmax}] Y[{dev.Ymin}..{dev.Ymax}]"
                + $" contactLinks={string.Join(",", dev.ContactLinks)}");
        }
        if (!tip && !inRange) return;
        (double x, double y) = ToScreen(dev, rawX, rawY);
        bool wasDown = _down.TryGetValue(id, out var d) && d;
        bool nowDown = tip;
        if (nowDown != wasDown)
        {
            _down[id] = nowDown;
            DebugLog.Write($"RAWINPUT contact id={id} {(nowDown ? "DOWN" : "UP")}"
                + $" raw=({rawX},{rawY}) -> screen=({x:0},{y:0})"
                + $" others=[{string.Join(",", _down.Keys)}]");
        }
        Contact?.Invoke(id, x, y, nowDown);
    }

    /// <summary>
    /// Device units -> screen pixels. A touch screen reports absolute positions
    /// across the display; a touch pad reports its own 0..1 surface, which is
    /// placed over the pad's rectangle so the rest of the code sees the same
    /// space it always used.
    /// </summary>
    private (double X, double Y) ToScreen(Device dev, uint rawX, uint rawY)
    {
        double nx = Map(dev.Xmin, dev.Xmax, rawX);
        double ny = Map(dev.Ymin, dev.Ymax, rawY);
        if (dev.Caps.Usage == USAGE_TOUCH_PAD && PadRectProvider != null)
        {
            var r = PadRectProvider();
            return (r.X + nx * r.W, r.Y + ny * r.H);
        }
        return (nx * GetSystemMetrics(0), ny * GetSystemMetrics(1));
    }

    private uint Read(Device dev, ushort link, ushort usage, IntPtr report,
        uint length)
    {
        try
        {
            if (HidP_GetUsageValue(HidP_Input, HID_USAGE_PAGE_DIGITIZER, link,
                usage, out uint v, dev.Preparsed, report, length) >= 0)
                return v;
        }
        catch { }
        return 0;
    }

    private static double Map(int min, int max, uint value)
    {
        if (max <= min) return value;
        double t = (value - (double)min) / (max - (double)min);
        // Digitizers report in their own units; 0..1000 is common for a
        // normalised touchpad, 0..32767 for a raw panel. The log below prints
        // both so the range can be corrected from a real run.
        return Math.Clamp(t, 0, 1);
    }
}
