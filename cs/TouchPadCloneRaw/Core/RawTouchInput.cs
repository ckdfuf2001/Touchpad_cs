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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels, dmBitsPerPel;
        public uint dmPelsWidth, dmPelsHeight;
        public uint dmDisplayFlags, dmDisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? device,
        int mode, ref DEVMODE devMode);
    private const int ENUM_CURRENT_SETTINGS = -1;

    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_DEVNOTIFY = 0x00002000;
    private const int RIM_TYPEHID = 2;

    private const ushort HID_USAGE_PAGE_DIGITIZER = 0x0D;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort USAGE_TOUCH_SCREEN = 0x04;
    private const ushort USAGE_TOUCH_PAD = 0x05;

    private const ushort USAGE_CONTACT_ID = 0x51;   // Contact Identifier
    private const ushort USAGE_TIP_SWITCH = 0x42;
    private const ushort USAGE_IN_RANGE = 0x32;
    /// <summary>
    /// Absolute position lives on the GENERIC DESKTOP page: usage 0x30 = X,
    /// 0x31 = Y. On the Digitizer page 0x30/0x31 are Tip/Barrel PRESSURE - on
    /// this panel they read 0..255, which is why the cursor barely moved: X was
    /// pressure (max 255) and Y never matched at all (Y caps were skipped, so
    /// Y mapped off-screen). Measured caps: page 1 use 0x30 [0..1600],
    /// use 0x31 [0..2560].
    /// </summary>
    private const ushort USAGE_GENERIC_X = 0x30;
    private const ushort USAGE_GENERIC_Y = 0x31;
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

    /// <summary>
    /// Subset of HIDP_BUTTON_CAPS. Reserved is ULONG[10] (40 bytes) - reading
    /// it as bytes shifted every field after it and produced use=0..0. With
    /// the right layout the union starts at offset 56 either way: range tail
    /// (UsageMin..DataIndexMax), or single usage (NotRange.Usage/DataIndex at
    /// the same offsets as UsageMin/DataIndexMin).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_BUTTON_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
        public uint[] Reserved;
        public ushort UsageMin, UsageMax;
        public ushort StringMin, StringMax, DesignatorMin, DesignatorMax,
            DataIndexMin, DataIndexMax;
    }

    /// <summary>
    /// One HidP_GetData entry: which control (DataIndex) + its value.
    /// Button entries appear in the list when ON; value entries always.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_DATA
    {
        public ushort DataIndex, Reserved;
        public uint RawValue;
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
    private static extern int HidP_GetButtonCaps(ushort reportType,
        [Out] HIDP_BUTTON_CAPS[] caps, ref ushort length, IntPtr preparsed);
    /// <summary>
    /// The reference's reader: one call returns every ON button plus every
    /// value in the report. Tip Switch / In Range are BUTTONS (HidP_GetUsageValue
    /// on them always fails - that is why no contact ever parsed), X / Y /
    /// Contact Identifier are values. DataIndex routes each entry to its link.
    /// </summary>
    [DllImport("hid.dll")]
    private static extern int HidP_GetData(ushort reportType,
        [In, Out] HIDP_DATA[] dataList, ref uint length, IntPtr preparsed,
        IntPtr report, uint reportLength);

    private sealed class Device
    {
        public IntPtr Preparsed;
        public HIDP_CAPS Caps;
        public HIDP_VALUE_CAPS[] ValueCaps = Array.Empty<HIDP_VALUE_CAPS>();
        public HIDP_BUTTON_CAPS[] ButtonCaps = Array.Empty<HIDP_BUTTON_CAPS>();
        public ushort[] ContactLinks = Array.Empty<ushort>();
        public int Xmin, Xmax, Ymin, Ymax;
        // Per link: data-index ranges routing GetData entries to contacts.
        public readonly Dictionary<ushort, (ushort Dmin, ushort Dmax)> TipByLink = new();
        public readonly Dictionary<ushort, (ushort Dmin, ushort Dmax)> RangeByLink = new();
        public readonly Dictionary<ushort, (ushort Dmin, ushort Dmax)> XByLink = new();
        public readonly Dictionary<ushort, (ushort Dmin, ushort Dmax)> YByLink = new();
        public readonly Dictionary<ushort, (ushort Dmin, ushort Dmax)> IdByLink = new();
        public readonly HashSet<ushort> ButtonIndices = new();
        public uint DataIndices;
        public bool Logged;
    }

    private readonly Dictionary<IntPtr, Device> _devices = new();
    private readonly Dictionary<int, bool> _down = new();
    private byte[]? _lastBytes;
    private bool _registered;
    private int _seen;
    private readonly int[] _typeCounts = new int[4];

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
                // Count by type. WPF registers raw input for the mouse as well,
                // so this window sees a flood of mouse messages too and the log
                // was unreadable with a "first five" rule.
                _typeCounts[Math.Min(3, (int)rawHeader.dwType)]++;
                if ((_seen % 500) == 0)
                {
                    DebugLog.Write($"RAWINPUT types: mouse={_typeCounts[0]}"
                        + $" kbd={_typeCounts[1]} hid={_typeCounts[2]}"
                        + $" other={_typeCounts[3]}");
                }
                if (rawHeader.dwType != RIM_TYPEHID) return false;
                if (_typeCounts[2] <= 3)
                {
                    DebugLog.Write($"RAWINPUT HID report size={rawHeader.dwSize}"
                        + $" dev={rawHeader.hDevice}");
                }
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
        if (size == 0)
        {
            DebugLog.Write($"RAWINPUT hid={hDevice} has no preparsed data"
                + " - not a HID device, or the report cannot be parsed");
            return null;
        }
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
        ushort nb = dev.Caps.NumberInputButtonCaps;
        if (nb > 0)
        {
            var caps = new HIDP_BUTTON_CAPS[nb];
            ushort len = nb;
            if (HidP_GetButtonCaps(HidP_Input, caps, ref len, prep) >= 0)
                dev.ButtonCaps = caps;
        }
        dev.DataIndices = dev.Caps.NumberInputDataIndices;
        // Route table: data-index range per usage per link collection.
        var links = new List<ushort>();
        foreach (var vc in dev.ValueCaps)
        {
            ushort umin = vc.UsageMin;
            ushort umax = vc.IsRange != 0 ? vc.UsageMax : vc.UsageMin;
            // X/Y come from the GENERIC DESKTOP page, not Digitizer (where
            // 0x30/0x31 are pressure). Contact Identifier stays on Digitizer.
            bool hasX = vc.UsagePage == HID_USAGE_PAGE_GENERIC
                && USAGE_GENERIC_X >= umin && USAGE_GENERIC_X <= umax;
            bool hasY = vc.UsagePage == HID_USAGE_PAGE_GENERIC
                && USAGE_GENERIC_Y >= umin && USAGE_GENERIC_Y <= umax;
            bool hasId = vc.UsagePage == HID_USAGE_PAGE_DIGITIZER
                && USAGE_CONTACT_ID >= umin && USAGE_CONTACT_ID <= umax;
            if (hasX || hasY || hasId)
            {
                if (!links.Contains(vc.LinkCollection))
                    links.Add(vc.LinkCollection);
                // Single-usage caps: DataIndexMax reads the NotRange.Reserved4
                // slot, so both ends are DataIndexMin.
                ushort dmin = vc.DataIndexMin;
                ushort dmax = vc.IsRange != 0 ? vc.DataIndexMax : vc.DataIndexMin;
                if (hasX)
                {
                    dev.XByLink[vc.LinkCollection] = (dmin, dmax);
                    if (vc.LogicalMax > vc.LogicalMin)
                    { dev.Xmin = vc.LogicalMin; dev.Xmax = vc.LogicalMax; }
                }
                if (hasY)
                {
                    dev.YByLink[vc.LinkCollection] = (dmin, dmax);
                    if (vc.LogicalMax > vc.LogicalMin)
                    { dev.Ymin = vc.LogicalMin; dev.Ymax = vc.LogicalMax; }
                }
                if (hasId)
                    dev.IdByLink[vc.LinkCollection] = (dmin, dmax);
            }
        }
        foreach (var bc in dev.ButtonCaps)
        {
            if (bc.UsagePage != HID_USAGE_PAGE_DIGITIZER) continue;
            ushort umin = bc.UsageMin;
            ushort umax = bc.IsRange != 0 ? bc.UsageMax : bc.UsageMin;
            var range = bc.IsRange != 0
                ? (bc.DataIndexMin, bc.DataIndexMax)
                : (bc.DataIndexMin, bc.DataIndexMin);
            for (ushort d = range.Item1; d <= range.Item2; d++)
                dev.ButtonIndices.Add(d);
            if (umin <= USAGE_TIP_SWITCH && USAGE_TIP_SWITCH <= umax)
                dev.TipByLink[bc.LinkCollection] = range;
            if (umin <= USAGE_IN_RANGE && USAGE_IN_RANGE <= umax)
                dev.RangeByLink[bc.LinkCollection] = range;
        }
        // Links with buttons but no value caps still count as contacts.
        foreach (var link in dev.TipByLink.Keys)
            if (!links.Contains(link)) links.Add(link);
        links.Sort();
        dev.ContactLinks = links.ToArray();
        _devices[hDevice] = dev;
        return dev;
    }

    private void Parse(Device dev, IntPtr report, uint length)
    {
        int n = dev.ContactLinks.Length;
        if (n == 0) return;
        // One HidP_GetData per report: every ON button plus every value.
        uint capacity = Math.Max(dev.DataIndices, (uint)1);
        var list = new HIDP_DATA[capacity];
        uint len = capacity;
        if (HidP_GetData(HidP_Input, list, ref len, dev.Preparsed,
            report, length) < 0)
            return;
        // Stash a copy of the raw bytes for transition dumps (tip forensics).
        try
        {
            int nb = (int)length;
            _lastBytes = new byte[nb];
            Marshal.Copy(report, _lastBytes, 0, nb);
        }
        catch { _lastBytes = null; }
        var values = new Dictionary<ushort, uint>((int)len);
        var buttons = new HashSet<ushort>();
        for (int i = 0; i < (int)len; i++)
        {
            values[list[i].DataIndex] = list[i].RawValue;
            // GetData mixes buttons and values in one list; only indices
            // from the button caps are buttons. Everything else is a value,
            // and treating values as buttons faked tip-ON permanently.
            if (dev.ButtonIndices.Contains(list[i].DataIndex))
                buttons.Add(list[i].DataIndex);
        }
        if (!dev.Logged)
        {
            dev.Logged = true;
            try
            {
                var dm = new DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
                if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
                    DebugLog.Write($"RAWINPUT display: {dm.dmPelsWidth}x{dm.dmPelsHeight}"
                        + $" orient={dm.dmDisplayOrientation} freq={dm.dmDisplayFrequency}");
            }
            catch { }
            DebugLog.Write($"RAWINPUT device caps: usePage={dev.Caps.UsagePage:X}"
                + $" use={dev.Caps.Usage:X} links={dev.Caps.NumberLinkCollectionNodes}"
                + $" valCaps={dev.Caps.NumberInputValueCaps}"
                + $" btnCaps={dev.Caps.NumberInputButtonCaps}"
                + $" dataIndices={dev.Caps.NumberInputDataIndices}"
                + $" reportBytes={dev.Caps.InputReportByteLength}"
                + $" X[{dev.Xmin}..{dev.Xmax}] Y[{dev.Ymin}..{dev.Ymax}]"
                + $" contactLinks={string.Join(",", dev.ContactLinks)}");
            foreach (var bc in dev.ButtonCaps)
            {
                if (bc.UsagePage != HID_USAGE_PAGE_DIGITIZER) continue;
                ushort buMax = bc.IsRange != 0 ? bc.UsageMax : bc.UsageMin;
                ushort bdMax = bc.IsRange != 0 ? bc.DataIndexMax : bc.DataIndexMin;
                DebugLog.Write($"RAWINPUT btnCap link={bc.LinkCollection}"
                    + $" use={bc.UsageMin:X}..{buMax:X} data={bc.DataIndexMin}..{bdMax}");
            }
            foreach (var vc in dev.ValueCaps)
            {
                if (vc.UsagePage != HID_USAGE_PAGE_DIGITIZER
                    && vc.UsagePage != 0x01) continue;
                ushort vuMax = vc.IsRange != 0 ? vc.UsageMax : vc.UsageMin;
                ushort vdMax = vc.IsRange != 0 ? vc.DataIndexMax : vc.DataIndexMin;
                DebugLog.Write($"RAWINPUT valCap link={vc.LinkCollection}"
                    + $" page={vc.UsagePage:X} use={vc.UsageMin:X}..{vuMax:X}"
                    + $" log=[{vc.LogicalMin}..{vc.LogicalMax}]"
                    + $" data={vc.DataIndexMin}..{vdMax}");
            }
            DebugLog.Write($"RAWINPUT sample entries={len}"
                + $" data=[{string.Join(",", values.Keys)}]");
            try
            {
                var bb = new byte[Math.Min(24, (int)length)];
                Marshal.Copy(report, bb, 0, bb.Length);
                DebugLog.Write("RAWINPUT bytes="
                    + BitConverter.ToString(bb).Replace("-", " "));
            }
            catch { }
        }
        for (int k = 0; k < n; k++)
            Emit(dev, dev.ContactLinks[k], k, values, buttons);
    }

    private static bool On(Dictionary<ushort, (ushort Dmin, ushort Dmax)> table,
        ushort link, HashSet<ushort> buttons)
    {
        if (!table.TryGetValue(link, out var r)) return false;
        for (ushort d = r.Dmin; d <= r.Dmax; d++)
            if (buttons.Contains(d)) return true;
        return false;
    }

    private static uint Val(Dictionary<ushort, (ushort Dmin, ushort Dmax)> table,
        ushort link, Dictionary<ushort, uint> values)
    {
        if (!table.TryGetValue(link, out var r)) return 0;
        // Multi-report-count usages repeat per count; the first carries it.
        for (ushort d = r.Dmin; d <= r.Dmax; d++)
            if (values.TryGetValue(d, out uint v)) return v;
        return 0;
    }

    private void Emit(Device dev, ushort link, int slot,
        Dictionary<ushort, uint> values, HashSet<ushort> buttons)
    {
        // Buttons: presence in the GetData list means ON. Values: RawValue.
        // State is per LINK (slot), not per contact id: an idle link whose
        // Contact Identifier reads 0 used to fall back to its slot number and
        // collide with a live contact's real id - one link's DOWN became
        // another link's UP on the next report, flapping DOWN/UP every few ms
        // on byte-identical reports. Fallbacks live at 100+slot, outside the
        // device's real 0..10 id range, so they can never collide.
        bool tip = On(dev.TipByLink, link, buttons);
        uint idRaw = Val(dev.IdByLink, link, values);
        int id = idRaw == 0 ? 100 + slot : (int)idRaw;
        bool wasDown = _down.TryGetValue(link, out var d) && d;
        if (tip != wasDown)
        {
            _down[link] = tip;
            uint rawX = Val(dev.XByLink, link, values);
            uint rawY = Val(dev.YByLink, link, values);
            (double ux, double uy) = ToScreen(dev, rawX, rawY);
            string bytes = _lastBytes != null
                ? BitConverter.ToString(_lastBytes).Replace("-", " ") : "?";
            DebugLog.Write($"RAWINPUT contact id={id} {(tip ? "DOWN" : "UP")}"
                + $" link={link} raw=({rawX},{rawY}) -> screen=({ux:0},{uy:0})"
                + $" bytes=[{bytes}]");
            Contact?.Invoke(id, ux, uy, tip);
            if (!tip) return;
        }
        else if (!tip)
        {
            return;  // idle link: silent, no moves
        }
        uint mx = Val(dev.XByLink, link, values);
        uint my = Val(dev.YByLink, link, values);
        (double x, double y) = ToScreen(dev, mx, my);
        Contact?.Invoke(id, x, y, true);
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
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        // Panel portrait (Xrange < Yrange) on a landscape screen: the panel
        // is mounted rotated - rawY runs along screen X, rawX along screen Y.
        // Display orient=3 (270°) here; corner taps put BOTH contacts on the
        // pad only for swap+Yflip, so: screenX = ny*sw, screenY = (1-nx)*sh.
        // (Plain swap left the second tap above the pad; direct left both
        // taps left of it.) Verified by pad-corner taps, not assumed.
        if (dev.Xmax < dev.Ymax && sw > sh)
            return (ny * sw, (1 - nx) * sh);
        return (nx * sw, ny * sh);
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
