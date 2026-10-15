using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 认证时服务端发来的横幅(法律声明、「密码将于 3 天后过期」)在开 shell 时作为提示写进终端。
/// 曾经宿主不设 <c>BannerHandler</c>,横幅被静默丢掉。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public sealed class SshServerBannersTests
{
    [TestMethod]
    public async Task 横幅按行变成提示_跳板链上的按到达先后()
    {
        SshServerBanners banners = new();
        await banners.OnBannerAsync("Authorized use only.\r\n\r\nAll activity is logged.\r\n", CancellationToken.None);
        await banners.OnBannerAsync("Your password will expire in 3 days.\n", CancellationToken.None);

        IReadOnlyList<ShellStreamNotice> notices = banners.TakeNotices();

        Assert.AreSequenceEqual(
            ["Authorized use only.", "All activity is logged.", "Your password will expire in 3 days."],
            [.. notices.Select(n => n.Text)]);
        Assert.DoesNotContain(n => n.IsWarning, notices);
    }

    /// <summary>标识串之前的前导行(规格 02 §3)排在认证横幅前面,同样逐行清洗。</summary>
    [TestMethod]
    public async Task 标识串之前的前导行排在认证横幅前面()
    {
        SshServerBanners banners = new();
        await banners.OnPreAuthBannerAsync(["Maintenance tonight 22:00.", "\e[31mRed"], CancellationToken.None);
        await banners.OnBannerAsync("Authorized use only.\n", CancellationToken.None);

        string[] texts = [.. banners.TakeNotices().Select(n => n.Text)];

        Assert.HasCount(3, texts);
        Assert.AreEqual("Maintenance tonight 22:00.", texts[0]);
        Assert.DoesNotContain("\e", texts[1]);
        Assert.AreEqual("Authorized use only.", texts[2]);
    }

    /// <summary>横幅来自未认证的对端:终端控制序列与双向控制符不许原样进终端。</summary>
    [TestMethod]
    public async Task 控制字符与双向控制符被去掉()
    {
        SshServerBanners banners = new();
        await banners.OnBannerAsync("\e[2J\e]0;pwned\aWelcome\u009b31m\u202Eevil\r", CancellationToken.None);

        string text = banners.TakeNotices().Single().Text;

        Assert.DoesNotContain(c => char.IsControl(c) || c == '\u202E', text, $"残留控制字符:{text}");
        Assert.Contains("Welcome", text);
    }

    [TestMethod]
    [DataRow("Banner")]
    [DataRow("DEBUG")]
    [DataRow("PreAuth")]
    public async Task 未知认证类型的横幅与DEBUG和前导行都先脱敏再缓存展示(string source)
    {
        // Banner 与 always_display DEBUG 都接到 OnBannerAsync；缓存不知道连接是否使用 Tailscale 认证。
        string[] candidates =
        [
            "https://login.tailscale.com/a/sensitive-token?state=sensitive-query",
            "https://login\u3002tailscale.com/a/sensitive-token?state=sensitive-query",
            "https://ｌｏｇｉｎ.ｔａｉｌｓｃａｌｅ.ｃｏｍ/a/sensitive-token?state=sensitive-query",
            "http://login\u3002tailscale.com/a/sensitive-token?state=sensitive-query",
            "https://sub.login\u3002tailscale.com/a/sensitive-token?state=sensitive-query",
            "http://login.tailscale.com/a/sensitive-token?state=sensitive-query",
            "https://login.tailscale.com.evil.example/a/sensitive-token?state=sensitive-query",
            "https://login.tailscale.com:444/a/sensitive-token?state=sensitive-query",
            "https://login.tailscale.com/a/sensitive-token\u202Esuffix?state=sensitive-query",
            "https://login.tailscale.com/a/sensitive-token\\suffix?state=sensitive-query",
            "https://login.tailscale.com/a/sensitive-token?state=" + new string('x', SshServerBanners.MaxLineLength * 2)
                + "sensitive-query"
        ];
        foreach (string candidate in candidates)
        {
            SshServerBanners banners = new();
            string[] lines =
            [
                "Authorized use only.",
                "Verify (" + candidate + ") and https://login.tailscale.com/a/second-secret?state=second-query",
                "All activity is logged. https://example.com/help?topic=ssh"
            ];
            if (source == "PreAuth")
            {
                await banners.OnPreAuthBannerAsync(lines, CancellationToken.None);
            }
            else
            {
                await banners.OnBannerAsync(string.Join('\n', lines), CancellationToken.None);
            }

            string text = string.Join('\n', banners.TakeNotices().Select(n => n.Text));

            Assert.DoesNotContain("sensitive-token", text);
            Assert.DoesNotContain("sensitive-query", text);
            Assert.DoesNotContain("second-secret", text);
            Assert.DoesNotContain("second-query", text);
            Assert.Contains("Authorized use only.", text);
            Assert.Contains("All activity is logged.", text);
            Assert.Contains("https://example.com/help?topic=ssh", text);
        }
    }

    [TestMethod]
    public async Task 只交出一次_之后再开的shell不重复显示()
    {
        SshServerBanners banners = new();
        await banners.OnBannerAsync("hello", CancellationToken.None);

        Assert.HasCount(1, banners.TakeNotices());
        Assert.IsEmpty(banners.TakeNotices());
    }

    [TestMethod]
    public async Task 行数行长与段数都有上限()
    {
        SshServerBanners banners = new();
        await banners.OnBannerAsync(
            string.Join('\n', Enumerable.Range(0, SshServerBanners.MaxLines + 10).Select(i => $"line {i}")),
            CancellationToken.None);

        IReadOnlyList<ShellStreamNotice> notices = banners.TakeNotices();
        Assert.HasCount(SshServerBanners.MaxLines + 1, notices);
        Assert.AreEqual("…", notices[^1].Text);

        await banners.OnBannerAsync(new string('x', SshServerBanners.MaxLineLength * 4), CancellationToken.None);
        Assert.AreEqual(SshServerBanners.MaxLineLength, banners.TakeNotices().Single().Text.Length);

        // 只建链不开 shell 时没人来取,段数不能无限攒。
        for (int i = 0; i < SshServerBanners.MaxTexts * 3; i++)
        {
            await banners.OnBannerAsync($"banner {i}", CancellationToken.None);
        }
        Assert.HasCount(SshServerBanners.MaxTexts, banners.TakeNotices());
    }
}
