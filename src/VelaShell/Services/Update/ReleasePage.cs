namespace VelaShell.Services.Update;

/// <summary>本仓库的发布页地址,以及「清单里给的发布页链接能不能直接打开」的判定。</summary>
/// <remarks>
/// <para>
/// 更新日志窗口的「在 GitHub 上查看」与 Linux / 不能原地换版时的「前往发布页」,打开的是 <c>latest.json</c> 里
/// <c>releaseNotes.htmlUrl</c> 给的地址(CI 用 <c>gh release view --json url</c> 写进去,就是那个版本的发布页)。
/// 原先只校验 https:清单被人换掉或 CI 写错时,这个按钮就能把用户带到任何一个 https 网站,
/// 而按钮上写的是「前往发布页」(#655 复审)。维护者 2026-10-25 定:只放行本仓库的 releases 页,
/// 不合格就退回打开本仓库的发布列表 —— 按钮照样有用,只是去不了别处。
/// </para>
/// <para>
/// 判定按解析之后的各个部分做,不按字符串前缀:<c>https://github.com.evil.example/…</c>、
/// <c>https://github.com@evil.example/…</c>、<c>…/releases/../../别的仓库</c> 这几种前缀对得上的写法都过不去。
/// 打开的是按核过的路径重新拼出的地址,原串里的其它部分一概不带。
/// </para>
/// </remarks>
public static class ReleasePage
{
    /// <summary>本仓库;更新源(<see cref="GitHubReleaseSource" />)与发布页都从这里来。</summary>
    public const string RepositoryUrl = "https://github.com/joesdu/VelaShell";

    /// <summary>发布列表页;清单里的链接不合格时打开它。</summary>
    public const string ReleasesUrl = RepositoryUrl + "/releases";

    /// <summary>发布列表在 github.com 上的路径(不含末尾斜杠),从 <see cref="ReleasesUrl" /> 取,仓库地址只写一处。</summary>
    private static readonly string ReleasesPath = new Uri(ReleasesUrl).AbsolutePath;

    /// <summary>清单里的发布页链接是本仓库的发布页就用它,否则退回本仓库的发布列表。</summary>
    /// <param name="htmlUrl">清单里的 <c>releaseNotes.htmlUrl</c>;可以缺。</param>
    public static Uri Resolve(string? htmlUrl) => TryGetRepositoryReleasePage(htmlUrl) ?? new Uri(ReleasesUrl);

    /// <summary>
    /// 是本仓库的发布页(<c>https://github.com/joesdu/VelaShell/releases</c> 本身或它下面的某一页)就返回按核过的路径重新拼出的地址,
    /// 否则为 null。要求:https、主机正好是 <c>github.com</c>、没有用户信息、默认端口、没有查询串与片段;
    /// 规范化之后的路径以本仓库的 <c>/releases</c> 开头,后面每一段只由字母、数字与 <c>. _ -</c> 组成且不是 <c>.</c> / <c>..</c>
    /// (版本标签的白名单本就是这些字符,见 <c>release.yml</c>;百分号转义一概不收)。
    /// 仓库名按 GitHub 的规矩不分大小写。
    /// </summary>
    /// <param name="url">待核的链接。</param>
    internal static Uri? TryGetRepositoryReleasePage(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length != 0
            || !uri.IsDefaultPort
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0)
        {
            return null;
        }
        string path = uri.AbsolutePath;
        if (!path.StartsWith(ReleasesPath, StringComparison.OrdinalIgnoreCase)) return null;
        string rest = path[ReleasesPath.Length..];
        // /releasesX 不是发布页;空段(//)也不收
        if ((rest.Length != 0 && rest[0] != '/') || rest.Contains("//", StringComparison.Ordinal)) return null;
        foreach (string segment in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or ".."
                || !segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return null;
            }
        }
        return new Uri("https://github.com" + path);
    }
}
