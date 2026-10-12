using System.Diagnostics.Metrics;

namespace VelaShell.Services.XServer;

/// <summary>
/// 内置 X 服务端在宿主这一侧的计量(仪表源 <see cref="MeterName" />,与服务端库的 <c>VelaShell.XServer</c> 并列):
/// 每帧从服务端取了几次像素、取不到(服务端忙)几次,损伤从服务端报出来到像素拷进位图隔了多久,每次取像素(含等像素锁)用了多久。
/// 没人订阅时不记。量帧率与跟手程度用(<c>scripts/xserver/perf/perf.cs</c> 订阅它),不进日志。
/// </summary>
internal static class XServerHostMetrics
{
    /// <summary>仪表源名。</summary>
    public const string MeterName = "VelaShell.XServer.Host";

    private static readonly Meter SharedMeter = new(MeterName);

    /// <summary>从服务端取了像素、交给渲染的帧数(每个窗口每帧至多一次)。</summary>
    public static Counter<long> Frames { get; } =
        SharedMeter.CreateCounter<long>("velashell.xserver.host.frames", "{frame}", "Frames whose pixels were read from the server and handed to rendering");

    /// <summary>像素锁在限时之内拿不到、这一帧让给界面的次数。</summary>
    public static Counter<long> BusyFrames { get; } =
        SharedMeter.CreateCounter<long>("velashell.xserver.host.frames.busy", "{frame}", "Frames skipped because the pixel lock was busy past the time limit");

    /// <summary>这一帧取走的损伤里最早的那一批从服务端报出来(执行线程放锁之后)到像素拷进位图的时间。</summary>
    public static Histogram<double> DamageLatency { get; } =
        SharedMeter.CreateHistogram<double>("velashell.xserver.host.damage.latency", "ms", "Time from the server reporting damage to its pixels being copied into the bitmaps");

    /// <summary>每次取像素(<c>TryReadPixels</c>,含等像素锁)的耗时;这段时间服务端的执行线程停着。</summary>
    public static Histogram<double> ReadDuration { get; } =
        SharedMeter.CreateHistogram<double>("velashell.xserver.host.read.duration", "ms", "Time spent reading pixels, including waiting for the pixel lock");
}
