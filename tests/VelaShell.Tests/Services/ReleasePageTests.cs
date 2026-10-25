using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

/// <summary>
/// 「前往发布页」只放行本仓库的 releases 页(#655 复审,维护者 2026-10-25 定):前缀对得上而主机、用户信息、端口或路径不对的,
/// 一律退回本仓库的发布列表。
/// </summary>
[TestClass]
public sealed class ReleasePageTests
{
    [TestMethod]
    [DataRow("https://github.com/joesdu/VelaShell/releases/tag/v1.7.3")]
    [DataRow("https://github.com/joesdu/VelaShell/releases/tag/1.8.0-beta.1")]
    [DataRow("https://github.com/joesdu/VelaShell/releases")]
    [DataRow("https://github.com/joesdu/VelaShell/releases/")]
    [DataRow("https://github.com/joesdu/VelaShell/releases/latest")]
    [DataRow("https://GitHub.com/JoesDu/velashell/releases/tag/v1.7.3")]
    public void ThisRepositorysReleasePages_AreOpenedAsGiven(string url)
    {
        Uri resolved = ReleasePage.Resolve(url);

        Assert.AreEqual(new Uri(url), resolved);
        Assert.AreEqual("github.com", resolved.Host);
    }

    [TestMethod]
    [DataRow(null, "清单里没有")]
    [DataRow("", "空串")]
    [DataRow("releases/tag/v1.7.3", "相对地址")]
    [DataRow("javascript:alert(1)", "不是网址")]
    [DataRow("http://github.com/joesdu/VelaShell/releases/tag/v1.7.3", "不是 https")]
    [DataRow("https://github.com/owner/repo/releases/tag/v1.7.3", "别的仓库")]
    [DataRow("https://github.com/joesdu/VelaShellX/releases/tag/v1.7.3", "名字以本仓库开头的别的仓库")]
    [DataRow("https://github.com/joesdu/VelaShell/releasesX", "releases 后面直接跟字符")]
    [DataRow("https://github.com/joesdu/VelaShell/issues/1", "本仓库的别的页面")]
    [DataRow("https://github.com/joesdu/VelaShell/releases/../../evil/repo/releases", "点段把路径带到别的仓库")]
    [DataRow("https://github.com/joesdu/VelaShell/releases/%2e%2e/%2e%2e/evil/repo", "转义的点段")]
    [DataRow("https://github.com/joesdu/VelaShell/releases/tag/v1%2F..", "转义的斜杠")]
    [DataRow("https://github.com/joesdu/VelaShell/releases//evil.example", "空段")]
    [DataRow("https://github.com.evil.example/joesdu/VelaShell/releases", "主机名以 github.com 开头")]
    [DataRow("https://evil.example/joesdu/VelaShell/releases", "别的站点")]
    [DataRow("https://github.com@evil.example/joesdu/VelaShell/releases", "用户信息里藏着 github.com")]
    [DataRow("https://someone@github.com/joesdu/VelaShell/releases", "带用户信息")]
    [DataRow("https://github.com:8443/joesdu/VelaShell/releases", "非默认端口")]
    [DataRow("https://github.com/joesdu/VelaShell/releases?next=https://evil.example", "带查询串")]
    [DataRow("https://github.com/joesdu/VelaShell/releases#x", "带片段")]
    [DataRow("https://xn--gthub-n4a.com/joesdu/VelaShell/releases", "形近的国际化域名")]
    public void AnythingElse_FallsBackToThisRepositorysReleaseList(string? url, string why)
    {
        Assert.AreEqual(new Uri(ReleasePage.ReleasesUrl), ReleasePage.Resolve(url), why);
    }

    [TestMethod]
    public void TheFallbackItselfPassesTheCheck()
    {
        // 退回的那个地址自己也要过得了判定,否则改了仓库地址之后按钮会在两个地址之间各说各话
        Assert.AreEqual(ReleasePage.ReleasesUrl, ReleasePage.TryGetRepositoryReleasePage(ReleasePage.ReleasesUrl)?.AbsoluteUri);
    }
}
