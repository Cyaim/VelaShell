namespace VelaShell.Services.Update;

/// <summary>Linux 检查更新后要打开的地址:对应安装包的直接下载链接,或者退回的发布页。</summary>
/// <param name="Url">要在浏览器里打开的地址。</param>
/// <param name="AssetName">直接下载的那个产物名;退回发布页时为 null。</param>
public sealed record LinuxUpdateLink(Uri Url, string? AssetName);

/// <summary>
/// Linux「检查更新」按当前的安装方式给出发布页上的那一个安装包(<c>plan.md</c> §172 原先只打开整页发布列表,
/// 让用户自己在 deb / rpm / AppImage / 便携包里挑)。
/// </summary>
/// <remarks>
/// <para>
/// 产物名照 <c>.github/workflows/release.yml</c> 与 <c>build/</c> 下打包脚本的命名拼:便携包直接取清单里登记的
/// tar.gz(应用内更新器的资产,本就在 <c>latest.json</c> 里);AppImage 是 <c>VelaShell-&lt;版本&gt;-&lt;RID&gt;.AppImage</c>;
/// deb / rpm 跟随发行版惯例(<c>velashell_1.2.3_amd64.deb</c> / <c>velashell-1.2.3-1.x86_64.rpm</c>)。
/// </para>
/// <para>
/// 预发布版的 deb / rpm 版本号里第一个 <c>-</c> 换成了 <c>~</c>(<c>build/linux-packages/README.md</c>),而 GitHub 附件名
/// 对这类字符的处理没有可依赖的约定(仓库也还没发过预发布版可供核对)—— 不去猜,打开那个版本的发布页。
/// 认不出安装方式、架构不受支持、清单里没有标签时,照旧打开整页发布列表。
/// </para>
/// </remarks>
public static class LinuxReleaseAsset
{
    /// <summary>发布列表页;认不出安装方式时打开它(§172 的原行为)。</summary>
    public const string ReleasesUrl = ReleasePage.ReleasesUrl;

    /// <summary>算出这一次该打开的地址。</summary>
    /// <param name="kind">当前的安装方式(<see cref="AppPackaging.LinuxInstall" />)。</param>
    /// <param name="manifest">检查选定的清单(<see cref="IUpdateService.AvailableUpdate" />);没有时退回发布列表。</param>
    /// <param name="rid">当前进程的 RID(<see cref="UpdateManifest.CurrentRid" />)。</param>
    /// <returns>要打开的地址,以及直接下载的那个产物名(退回发布页时为 null)。</returns>
    public static LinuxUpdateLink Resolve(LinuxInstallKind kind, UpdateManifest? manifest, string? rid)
    {
        if (kind == LinuxInstallKind.Unknown || manifest is null || string.IsNullOrEmpty(manifest.Tag))
        {
            return new(new Uri(ReleasesUrl), null);
        }
        string tag = Uri.EscapeDataString(manifest.Tag);
        return NameFor(kind, manifest, rid) is { } name
            ? new(new Uri($"{ReleasesUrl}/download/{tag}/{name}"), name)
            : new(new Uri($"{ReleasesUrl}/tag/{tag}"), null);
    }

    /// <summary>这种安装方式对应的产物名;拼不出来(或拼出的名字不是普通文件名)时为 null。</summary>
    internal static string? NameFor(LinuxInstallKind kind, UpdateManifest manifest, string? rid)
    {
        (string deb, string rpm)? arch = rid switch
        {
            "linux-x64" => ("amd64", "x86_64"),
            "linux-arm64" => ("arm64", "aarch64"),
            _ => null
        };
        if (arch is not var (debArch, rpmArch))
        {
            return null;
        }
        string version = manifest.Version;
        string? name = kind switch
        {
            LinuxInstallKind.Portable => manifest.Assets.TryGetValue(rid!, out UpdateAsset? asset) ? asset.Name : null,
            LinuxInstallKind.AppImage => $"VelaShell-{version}-{rid}.AppImage",
            LinuxInstallKind.Deb => $"velashell_{PackageVersion(version)}_{debArch}.deb",
            LinuxInstallKind.Rpm => $"velashell-{PackageVersion(version)}-1.{rpmArch}.rpm",
            _ => null
        };
        return name is not null && IsPlainFileName(name) ? name : null;
    }

    /// <summary>打包脚本的 <c>${version/-/\~}</c>:预发布后缀的第一个 <c>-</c> 换成 <c>~</c>,好让 dpkg / rpm 把它排在正式版之前。</summary>
    private static string PackageVersion(string version)
    {
        int dash = version.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? version : string.Concat(version.AsSpan(0, dash), "~", version.AsSpan(dash + 1));
    }

    /// <summary>与更新器认的附件名同一把尺:字母数字开头,只含字母数字、点、短横与下划线。</summary>
    private static bool IsPlainFileName(string name) =>
        name.Length is > 0 and <= 255
        && char.IsAsciiLetterOrDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
