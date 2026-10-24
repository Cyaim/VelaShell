using System.Text.Json;
using VelaShell.Services;
using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

/// <summary>
/// Linux「检查更新」按安装方式给出对应安装包:认安装方式(<see cref="AppPackaging.DetectLinuxInstall" />)
/// 与拼产物名(<see cref="LinuxReleaseAsset" />)。两样都是纯函数,探测由测试注入,任何平台上都能跑。
/// </summary>
/// <remarks>产物名对照的是 1.7.3 Release 上的实际附件名(<c>gh release view 1.7.3</c>)。</remarks>
[TestClass]
[TestCategory("Update")]
public sealed class LinuxReleaseAssetTests
{
    private static readonly Func<string, bool> Nothing = static _ => false;

    private static LinuxInstallKind Detect(string baseDirectory, string? appImage = null, string? appDir = null,
        Func<string, bool>? fileExists = null, Func<string, bool>? directoryExists = null) =>
        AppPackaging.DetectLinuxInstall(appImage, appDir, baseDirectory, fileExists ?? Nothing, directoryExists ?? Nothing);

    [TestMethod]
    public void AppImage_IsRecognizedFromTheRuntimeVariables() =>
        Assert.AreEqual(LinuxInstallKind.AppImage, Detect("/tmp/.mount_VelaShAbc123/usr/bin/",
            appImage: "/home/u/Apps/VelaShell-1.7.3-linux-x64.AppImage", appDir: "/tmp/.mount_VelaShAbc123"));

    /// <summary>从别的 AppImage 里开的终端把 APPIMAGE 带了进来,但我们并不在那个挂载点里:不算 AppImage。</summary>
    [TestMethod]
    public void AppImageVariablesInheritedFromAnotherAppImage_AreIgnored() =>
        Assert.AreEqual(LinuxInstallKind.Portable, Detect("/home/u/VelaShell/",
            appImage: "/home/u/Apps/Other.AppImage", appDir: "/tmp/.mount_OtherXyz"));

    [TestMethod]
    [DataRow("/var/lib/dpkg/info/velashell.list")]
    [DataRow("/var/lib/dpkg/info/velashell:amd64.list")]
    public void SystemInstallWithADpkgRecord_IsDeb(string dpkgRecord) =>
        Assert.AreEqual(LinuxInstallKind.Deb, Detect("/opt/velashell/",
            fileExists: path => path == dpkgRecord,
            // Debian 上装了 rpm 工具也会有 rpm 数据库:dpkg 记录在就是 deb。
            directoryExists: path => path == "/var/lib/rpm"));

    [TestMethod]
    [DataRow("/var/lib/rpm")]
    [DataRow("/usr/lib/sysimage/rpm")]
    public void SystemInstallWithoutDpkgButWithAnRpmDatabase_IsRpm(string rpmDatabase) =>
        Assert.AreEqual(LinuxInstallKind.Rpm, Detect("/opt/velashell/", directoryExists: path => path == rpmDatabase));

    [TestMethod]
    public void SystemInstallWithNoPackageManagerRecord_IsUnknown() =>
        Assert.AreEqual(LinuxInstallKind.Unknown, Detect("/opt/velashell/"));

    [TestMethod]
    [DataRow("/home/u/VelaShell-1.7.3-linux-x64/")]
    [DataRow("/opt/velashell-portable/")]
    public void AnyOtherDirectory_IsThePortableTarball(string baseDirectory) =>
        Assert.AreEqual(LinuxInstallKind.Portable, Detect(baseDirectory));

    [TestMethod]
    [DataRow(LinuxInstallKind.Deb, "linux-x64", "velashell_1.7.3_amd64.deb")]
    [DataRow(LinuxInstallKind.Deb, "linux-arm64", "velashell_1.7.3_arm64.deb")]
    [DataRow(LinuxInstallKind.Rpm, "linux-x64", "velashell-1.7.3-1.x86_64.rpm")]
    [DataRow(LinuxInstallKind.Rpm, "linux-arm64", "velashell-1.7.3-1.aarch64.rpm")]
    [DataRow(LinuxInstallKind.AppImage, "linux-x64", "VelaShell-1.7.3-linux-x64.AppImage")]
    [DataRow(LinuxInstallKind.AppImage, "linux-arm64", "VelaShell-1.7.3-linux-arm64.AppImage")]
    [DataRow(LinuxInstallKind.Portable, "linux-x64", "VelaShell-1.7.3-linux-x64.tar.gz")]
    [DataRow(LinuxInstallKind.Portable, "linux-arm64", "VelaShell-1.7.3-linux-arm64.tar.gz")]
    public void KnownInstall_LinksStraightToItsPackage(LinuxInstallKind kind, string rid, string expected)
    {
        LinuxUpdateLink link = LinuxReleaseAsset.Resolve(kind, Manifest("1.7.3"), rid);

        Assert.AreEqual(expected, link.AssetName);
        Assert.AreEqual($"https://github.com/joesdu/VelaShell/releases/download/1.7.3/{expected}", link.Url.AbsoluteUri);
    }

    /// <summary>
    /// 预发布版的 deb / rpm 名字里有 <c>~</c>(打包脚本让它排在正式版之前),GitHub 附件名怎么处理这个字符
    /// 没有可依赖的约定:不猜,打开那个版本的发布页。AppImage 与便携包的名字照原样带版本号,照样直链。
    /// </summary>
    [TestMethod]
    [DataRow(LinuxInstallKind.Deb, null)]
    [DataRow(LinuxInstallKind.Rpm, null)]
    [DataRow(LinuxInstallKind.AppImage, "VelaShell-2.0.0-preview.1-linux-x64.AppImage")]
    [DataRow(LinuxInstallKind.Portable, "VelaShell-2.0.0-preview.1-linux-x64.tar.gz")]
    public void PreRelease_DebAndRpmFallBackToTheTagPage(LinuxInstallKind kind, string? expected)
    {
        LinuxUpdateLink link = LinuxReleaseAsset.Resolve(kind, Manifest("2.0.0-preview.1"), "linux-x64");

        Assert.AreEqual(expected, link.AssetName);
        Assert.AreEqual(expected is null
                ? "https://github.com/joesdu/VelaShell/releases/tag/2.0.0-preview.1"
                : $"https://github.com/joesdu/VelaShell/releases/download/2.0.0-preview.1/{expected}",
            link.Url.AbsoluteUri);
    }

    /// <summary>认不出安装方式、或者手里没有检查选定的清单:照旧打开整页发布列表(§172)。</summary>
    [TestMethod]
    public void UnknownInstallOrNoManifest_OpensTheReleaseList()
    {
        Assert.AreEqual(LinuxReleaseAsset.ReleasesUrl,
            LinuxReleaseAsset.Resolve(LinuxInstallKind.Unknown, Manifest("1.7.3"), "linux-x64").Url.AbsoluteUri);
        Assert.AreEqual(LinuxReleaseAsset.ReleasesUrl,
            LinuxReleaseAsset.Resolve(LinuxInstallKind.Deb, null, "linux-x64").Url.AbsoluteUri);
    }

    [TestMethod]
    public void UnsupportedArchitecture_FallsBackToTheTagPage()
    {
        LinuxUpdateLink link = LinuxReleaseAsset.Resolve(LinuxInstallKind.Deb, Manifest("1.7.3"), "linux-riscv64");

        Assert.IsNull(link.AssetName);
        Assert.AreEqual("https://github.com/joesdu/VelaShell/releases/tag/1.7.3", link.Url.AbsoluteUri);
    }

    /// <summary>与发布流水线一样:latest.json 里只登记 tar.gz(应用内更新器的资产)。</summary>
    private static UpdateManifest Manifest(string version) =>
        UpdateManifest.Parse(JsonSerializer.Serialize(new
        {
            version,
            tag = version,
            assets = new Dictionary<string, object>
            {
                ["linux-x64"] = new { name = $"VelaShell-{version}-linux-x64.tar.gz", sha256 = new string('a', 64), size = 100 },
                ["linux-arm64"] = new { name = $"VelaShell-{version}-linux-arm64.tar.gz", sha256 = new string('b', 64), size = 100 },
            }
        }));
}
