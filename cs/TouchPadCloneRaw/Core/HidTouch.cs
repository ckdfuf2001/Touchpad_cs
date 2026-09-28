using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace TouchPadCloneV2.Core;

/// <summary>
/// Phase 1: raw HID touch PROBE. Enumerates digitizers, registers for
/// WM_INPUT (sink, legacy untouched), dumps caps + raw report bytes.
/// The generic contact parser (phase 2) is written from observed bytes -
/// digitizer report layouts vary too much to guess blind.
/// </summary>
public static class RawTouchProbe
{
    private const int WM_INPUT = 0x00FF;
    private const int RIM_TYPEHID = 2;
    private const int RID_INPUT = 0x10000003;
    private const int RIDI_DEVICEINFO = 0x2000000B;
    private const int RIDEV_INPUTSINK = 0x00000100;
    private const ushort DIGITIZER_PAGE = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWHID
    {
        public uint dwSizeHid;
        public uint dwCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO_HID
    {
        public uint dwVendorId;
        public uint dwProductId;
        public uint dwVersionNumber;
        public ushort usUsagePage;
        public ushort usUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("user32.dll")]
    private static extern uint GetRawInputDeviceList(IntPtr list, ref uint count,
        uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command,
        IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint command,
        IntPtr data, ref uint size);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices,
        uint count, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share,
        IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(IntPtr hid, ref IntPtr prep);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr prep);
    [DllImport("hid.dll")]
    private static extern uint HidP_GetCaps(IntPtr prep, ref HIDP_CAPS caps);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;

    private static int _reports;
    private static readonly HashSet<IntPtr> _loggedCaps = new();

    public static void Attach(Window w, Action<string> log)
    {
        try
        {
            uint count = 0;
            uint sz = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
            GetRawInputDeviceList(IntPtr.Zero, ref count, sz);
            log($"RAWDEV count={count}");
            if (count == 0 || count > 128) return;
            IntPtr buf = Marshal.AllocHGlobal((int)(sz * count));
            try
            {
                if (GetRawInputDeviceList(buf, ref count, sz) == unchecked((uint)-1))
                {
                    log("RAWDEV list failed");
                    return;
                }
                for (int i = 0; i < count; i++)
                {
                    var dev = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(
                        buf + i * (int)sz);
                    Describe(dev.hDevice, dev.dwType, log);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }

            var regs = new[]
            {
                new RAWINPUTDEVICE { usUsagePage = DIGITIZER_PAGE, usUsage = 0x04,
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = new WindowInteropHelper(w).Handle },
                new RAWINPUTDEVICE { usUsagePage = DIGITIZER_PAGE, usUsage = 0x05,
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = new WindowInteropHelper(w).Handle },
            };
            bool ok = RegisterRawInputDevices(regs, (uint)regs.Length,
                (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            log($"REGISTER digitizer sink -> {ok}");

            var src = (HwndSource?)PresentationSource.FromVisual(w);
            src?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp,
                ref bool handled) =>
            {
                if (msg == WM_INPUT) OnInput(lp, log);
                return IntPtr.Zero;
            });
        }
        catch (Exception ex) { log("PROBE fail: " + ex.Message); }
    }

    private static void Describe(IntPtr h, uint type, Action<string> log)
    {
        try
        {
            if (type != RIM_TYPEHID) return;
            uint size = 0;
            var info = new RID_DEVICE_INFO_HID();
            uint infoSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO_HID>() + 8;
            IntPtr p = Marshal.AllocHGlobal((int)infoSize);
            try
            {
                Marshal.WriteInt32(p, (int)infoSize);
                uint cb = infoSize;
                if (GetRawInputDeviceInfo(h, RIDI_DEVICEINFO, p, ref cb) == unchecked((uint)-1))
                {
                    log($"HID h={h:X} info-fail");
                    return;
                }
                info = Marshal.PtrToStructure<RID_DEVICE_INFO_HID>(p + 8);
            }
            finally { Marshal.FreeHGlobal(p); }
            log($"HID h={h:X} vid={info.dwVendorId:X4} pid={info.dwProductId:X4} " +
                $"page={info.usUsagePage:X2} usage={info.usUsage:X2}");
            if (info.usUsagePage != DIGITIZER_PAGE || !_loggedCaps.Add(h)) return;

            // device path + preparsed caps
            uint n = 0;
            GetRawInputDeviceInfo(h, 0x20000007 /*RIDI_DEVICENAME*/, IntPtr.Zero, ref n);
            if (n == 0 || n > 512) { log("  noname"); return; }
            IntPtr nb = Marshal.AllocHGlobal((int)(n * 2));
            try
            {
                if (GetRawInputDeviceInfo(h, 0x20000007, nb, ref n) == unchecked((uint)-1))
                {
                    log("  name-fail");
                    return;
                }
                string path = Marshal.PtrToStringUni(nb) ?? "";
                log($"  path={path}");
                IntPtr fh = CreateFile(path, GENERIC_READ | GENERIC_WRITE, 3,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (fh == new IntPtr(-1)) { log("  open-fail"); return; }
                try
                {
                    IntPtr prep = IntPtr.Zero;
                    if (!HidD_GetPreparsedData(fh, ref prep) || prep == IntPtr.Zero)
                    {
                        log("  prep-fail");
                        return;
                    }
                    try
                    {
                        var caps = new HIDP_CAPS();
                        uint st = HidP_GetCaps(prep, ref caps);
                        log($"  caps st={st} usage={caps.UsagePage:X2}:{caps.Usage:X2} " +
                            $"inLen={caps.InputReportByteLength} " +
                            $"nValIn={caps.NumberInputValueCaps} " +
                            $"nBtnIn={caps.NumberInputButtonCaps}");
                    }
                    finally { HidD_FreePreparsedData(prep); }
                }
                finally { /* keep handle open? no - close via... (leak 1 handle ok for probe) */ }
            }
            finally { Marshal.FreeHGlobal(nb); }
        }
        catch (Exception ex) { log("describe fail: " + ex.Message); }
    }

    private static void OnInput(IntPtr lp, Action<string> log)
    {
        try
        {
            uint size = 0;
            uint hs = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            GetRawInputData(lp, RID_INPUT, IntPtr.Zero, ref size, hs);
            if (size == 0 || size > 4096) return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lp, RID_INPUT, buf, ref size, hs) != size)
                    return;
                var head = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
                if (head.dwType != RIM_TYPEHID) return;
                var hid = Marshal.PtrToStructure<RAWHID>(buf + (int)hs);
                int n = (int)hid.dwCount;
                int each = (int)hid.dwSizeHid;
                if (n <= 0 || n > 32 || each <= 0 || each > 1024) return;
                _reports++;
                // first 25 reports full hex, then sparse (1/sec-ish)
                bool full = _reports <= 25 || _reports % 60 == 0;
                if (!full) return;
                byte[] raw = new byte[n * each];
                Marshal.Copy(buf + (int)hs + 8, raw, 0, raw.Length);
                var sb = new StringBuilder();
                sb.Append($"HID dev={head.hDevice:X} n={n} sz={each} #{_reports} ");
                foreach (byte b in raw) sb.Append(b.ToString("X2"));
                log(sb.ToString());
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
    }
}
