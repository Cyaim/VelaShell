using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Platform;

namespace VelaShell.Services.XServer;

/// <summary>
/// 显示器此刻的刷新率(Hz),报给服务端的 RANDR(<see cref="VelaShell.XServer.XMonitor.RefreshRate" />):GTK、Qt、浏览器按它定帧节奏 ——
/// 原先一律报 60,120 / 144 Hz 的屏上远端程序照样只画 60 帧。Windows 经显示器句柄(HMONITOR)取设备名、读当前显示模式;
/// macOS / Linux 先不做(返回 null,服务端按 60 报)。
/// </summary>
internal static unsafe partial class DisplayRefreshRate
{
    /// <summary><paramref name="screen" /> 的刷新率;取不到时 null。</summary>
    public static int? Of(Screen screen)
    {
        if (!OperatingSystem.IsWindows() || screen.TryGetPlatformHandle() is not { Handle: not 0 } handle)
        {
            return null;
        }
        try
        {
            return OfMonitor(handle.Handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int? OfMonitor(nint monitor)
    {
        MonitorInfoEx info = new() { Size = (uint)sizeof(MonitorInfoEx) };
        if (!GetMonitorInfoW(monitor, &info))
        {
            return null;
        }
        DevMode mode = new() { Size = (ushort)sizeof(DevMode) };
        if (!EnumDisplaySettingsW(info.Device, EnumCurrentSettings, &mode))
        {
            return null;
        }
        // 0 与 1 表示「硬件默认」,说不出具体是多少
        return mode.DisplayFrequency > 1 ? (int)mode.DisplayFrequency : null;
    }

    private const int EnumCurrentSettings = -1;

    /// <summary>MONITORINFOEXW。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public fixed int Monitor[4];
        public fixed int Work[4];
        public uint Flags;
        public fixed char Device[32];
    }

    /// <summary>DEVMODEW(显示设备的那一种联合体布局),共 220 字节;只用到 <see cref="DisplayFrequency" />。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DevMode
    {
        public fixed char DeviceName[32];
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TtOption, Collate;
        public fixed char FormName[32];
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, MonitorInfoEx* info);

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettingsW(char* deviceName, int modeNumber, DevMode* mode);
}
