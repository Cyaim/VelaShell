using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VelaShell.Services;

/// <summary>
/// 判断本进程是否以 MSIX 包身份运行(即从 Microsoft Store 安装的版本)。
/// <para>
/// 便携版与商店版共用同一份源码、同一套二进制,两者的行为差异全部由这里的<b>运行时</b>判断驱动,
/// 而不是编译期常量 —— 这样就不存在"商店构建误发到 GitHub Releases"或反过来的事故,
/// CI 也不必维护两套编译配置:同样的发布产物,打进 MSIX 就是商店版,压成 zip 就是便携版。
/// </para>
/// <para>
/// 目前唯一的差异是自更新:MSIX 装在 <c>C:\Program Files\WindowsApps</c>,该目录只读且 ACL 锁死,
/// 换版根本无从谈起;商店政策也要求包应用只能经商店更新。见 <see cref="IUpdateService.IsStoreManaged" />。
/// </para>
/// </summary>
internal static partial class AppPackaging
{
    /// <summary>缓冲区不足 —— 说明确实取到了包全名(本进程有包身份)。</summary>
    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>本进程没有包身份(便携版直接运行的常态)。</summary>
    private const uint AppModelErrorNoPackage = 15700;

    /// <summary>本进程是否以 MSIX 包身份运行。进程内不会变化,构造一次即可。</summary>
    public static bool IsPackaged { get; } = DetectPackageIdentity();

    /// <summary>
    /// 以长度 0 的缓冲区试探 <c>GetCurrentPackageFullName</c>:有包身份时因装不下返回
    /// <see cref="ErrorInsufficientBuffer" />,没有则返回 <see cref="AppModelErrorNoPackage" />。
    /// 这是官方推荐的判定法,比查进程路径是否落在 WindowsApps 之类的启发式可靠。
    /// </summary>
    private static bool DetectPackageIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            int length = 0;
            uint result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
            if (result is ErrorInsufficientBuffer or AppModelErrorNoPackage)
            {
                return result == ErrorInsufficientBuffer;
            }
            Trace.WriteLine($"[VelaShell] Unexpected GetCurrentPackageFullName result {result}; assuming unpackaged.");
            return false;
        }
        catch (Exception ex)
        {
            // Windows 8 以前没有这个导出;取不到就按便携版处理,自更新自会因目录不可写而止步。
            Trace.WriteLine($"[VelaShell] Package identity probe failed, assuming unpackaged: {ex.Message}");
            return false;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName")]
    private static partial uint GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);

    /// <summary>deb / rpm 都把整个发布目录装进这里(<c>build/linux-packages/Stage-Payload.sh</c>)。</summary>
    internal const string LinuxSystemInstallDirectory = "/opt/velashell";

    /// <summary>
    /// Linux 上这份程序是怎么装的:决定「检查更新」给哪一个安装包的下载链接(§172 原先只打开整页发布列表)。
    /// 进程内不会变化,只在 Linux 上探测一次;其它平台恒为 <see cref="LinuxInstallKind.Unknown" />。
    /// </summary>
    public static LinuxInstallKind LinuxInstall => _linuxInstall.Value;

    private static readonly Lazy<LinuxInstallKind> _linuxInstall = new(() => OperatingSystem.IsLinux()
        ? DetectLinuxInstall(
            Environment.GetEnvironmentVariable("APPIMAGE"),
            Environment.GetEnvironmentVariable("APPDIR"),
            AppContext.BaseDirectory,
            File.Exists,
            Directory.Exists)
        : LinuxInstallKind.Unknown);

    /// <summary>
    /// 按运行时留下的痕迹判断安装方式。判断依据都是打包脚本自己定下的事实,不猜发行版:
    /// <list type="bullet">
    /// <item>AppImage:type2 runtime 把 <c>APPIMAGE</c> 设成 .AppImage 文件本身的路径、<c>APPDIR</c> 设成挂载点;
    /// 应用目录还得落在那个挂载点里,免得从别的 AppImage 里起的终端把变量带进来。</item>
    /// <item>deb / rpm:装进 <see cref="LinuxSystemInstallDirectory" />(两者是同一棵树,只有外壳不同),
    /// 再看包管理器的记录 —— dpkg 给每个包留 <c>/var/lib/dpkg/info/&lt;包名&gt;.list</c>;没有 dpkg 记录、
    /// 系统里有 rpm 数据库(<c>/var/lib/rpm</c>,Fedora 36 起在 <c>/usr/lib/sysimage/rpm</c>)就是 rpm。两样都对不上算不知道。</item>
    /// <item>其余位置:tar.gz 解出来的便携包(应用内更新器的资产也是它)。</item>
    /// </list>
    /// </summary>
    /// <param name="appImage">环境变量 <c>APPIMAGE</c>。</param>
    /// <param name="appDir">环境变量 <c>APPDIR</c>。</param>
    /// <param name="baseDirectory">应用目录(<see cref="AppContext.BaseDirectory" />)。</param>
    /// <param name="fileExists">文件探测(测试注入)。</param>
    /// <param name="directoryExists">目录探测(测试注入)。</param>
    internal static LinuxInstallKind DetectLinuxInstall(
        string? appImage, string? appDir, string baseDirectory,
        Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        string appDirectory = baseDirectory.TrimEnd('/');
        if (!string.IsNullOrEmpty(appImage)
            && (string.IsNullOrEmpty(appDir) || IsUnder(appDirectory, appDir.TrimEnd('/'))))
        {
            return LinuxInstallKind.AppImage;
        }
        if (string.Equals(appDirectory, LinuxSystemInstallDirectory, StringComparison.Ordinal))
        {
            // 包架构是 amd64 / arm64 的普通包时 dpkg 记作 velashell.list;万一按 Multi-Arch 装,带 :架构 后缀。
            if (fileExists("/var/lib/dpkg/info/velashell.list")
                || fileExists("/var/lib/dpkg/info/velashell:amd64.list")
                || fileExists("/var/lib/dpkg/info/velashell:arm64.list"))
            {
                return LinuxInstallKind.Deb;
            }
            return directoryExists("/var/lib/rpm") || directoryExists("/usr/lib/sysimage/rpm")
                ? LinuxInstallKind.Rpm
                : LinuxInstallKind.Unknown;
        }
        return LinuxInstallKind.Portable;
    }

    private static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.Ordinal) || path.StartsWith(root + "/", StringComparison.Ordinal);
}

/// <summary>Linux 上的安装方式,对应发布页上四种 Linux 产物(见 <c>.github/workflows/release.yml</c>)。</summary>
public enum LinuxInstallKind
{
    /// <summary>判断不出来(或不是 Linux):检查更新照旧打开整页发布列表。</summary>
    Unknown,

    /// <summary><c>apt install ./velashell_*.deb</c> 装进 <c>/opt/velashell</c>。</summary>
    Deb,

    /// <summary><c>dnf install ./velashell-*.rpm</c> 装进 <c>/opt/velashell</c>。</summary>
    Rpm,

    /// <summary>单文件 AppImage。</summary>
    AppImage,

    /// <summary>tar.gz 便携包,解到哪儿算哪儿。</summary>
    Portable
}
