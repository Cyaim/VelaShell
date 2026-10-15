using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Ssh;

[TestClass]
[TestCategory("Ssh")]
public sealed class TailscaleSshCheckUriTests
{
    [TestMethod]
    [DataRow("https://login.tailscale.com/a/secret?next=ssh&state=original%2Fvalue")]
    [DataRow("https://login.tailscale.com:443/a/secret?state=original")]
    [DataRow("HTTPS://LOGIN.TAILSCALE.COM/a/secret?state=Original")]
    [DataRow("https://login\u3002tailscale.com/a/secret?state=original%2Fvalue")]
    [DataRow("https://ｌｏｇｉｎ.ｔａｉｌｓｃａｌｅ.ｃｏｍ/a/secret?state=original%2Fvalue")]
    public void 官方HTTPS链接保留完整路径与查询(string text)
    {
        Assert.IsTrue(TailscaleSshCheckUri.TryGetVerificationUri(text, out Uri? uri));
        Assert.IsNotNull(uri);
        Assert.IsTrue(TailscaleSshCheckUri.IsAllowed(uri));
        Assert.AreEqual(text, uri.OriginalString);
    }

    [TestMethod]
    [DataRow("https://login\u3002tailscale.com/a/sensitive-token?state=sensitive-query")]
    [DataRow("https://ｌｏｇｉｎ.ｔａｉｌｓｃａｌｅ.ｃｏｍ/a/sensitive-token?state=sensitive-query")]
    public void 官方IDN归一化允许的链接也必须脱敏(string candidate)
    {
        Assert.IsTrue(TailscaleSshCheckUri.TryGetVerificationUri(candidate, out Uri? uri));
        Assert.IsNotNull(uri);
        Assert.IsTrue(TailscaleSshCheckUri.IsAllowed(uri));
        Assert.AreEqual(candidate, uri.OriginalString);

        string redacted = TailscaleSshCheckUri.RedactVerificationUris($"Authorized use only. {candidate} All activity is logged.");

        Assert.DoesNotContain("sensitive-token", redacted);
        Assert.DoesNotContain("sensitive-query", redacted);
        Assert.Contains("Authorized use only.", redacted);
        Assert.Contains("All activity is logged.", redacted);
    }

    [TestMethod]
    [DataRow("http://login.tailscale.com/a/secret")]
    [DataRow("https://user:password@login.tailscale.com/a/secret")]
    [DataRow("https://sub.login.tailscale.com/a/secret")]
    [DataRow("https://login.tailscale.com.evil.example/a/secret")]
    [DataRow("https://sub.login\u3002tailscale.com/a/secret")]
    [DataRow("https://ｌｏｇｉｎ.ｔａｉｌｓｃａｌｅ.ｃｏｍ.evil.example/a/secret")]
    [DataRow("https://login.tailscale.com:444/a/secret")]
    [DataRow("https://login.tailscale.com/a/")]
    [DataRow("https://login.tailscale.com/a/?state=secret")]
    [DataRow("https://login.tailscale.com/a/secret#fragment")]
    [DataRow("https://login.tailscale.com\\a\\secret")]
    [DataRow("https://login.tailscale.com/a/sec\\ret")]
    [DataRow("https://login.tailscale.com/a/sec\tret")]
    [DataRow("https://login.tailscale.com/a/sec\u0001ret")]
    [DataRow("https://login.tailscale.com/a/sec\u200Bret")]
    [DataRow("https://login.tailscale.com/a/sec\u202Eret")]
    [DataRow("https://login.tailscale.com/a/sec\U000E0001ret")]
    public void 不安全候选不能截成可打开的前缀(string text)
    {
        Assert.IsFalse(TailscaleSshCheckUri.TryGetVerificationUri(text, out Uri? uri));
        Assert.IsNull(uri);
        if (Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed))
        {
            Assert.IsFalse(TailscaleSshCheckUri.IsAllowed(parsed));
        }
    }

    [TestMethod]
    [DataRow("\"{0}\"")]
    [DataRow("'{0}'")]
    [DataRow("({0})")]
    [DataRow("\"({0})\"")]
    public void 文本引号与括号仅移除外部包装(string wrapper)
    {
        const string url = "https://login.tailscale.com/a/secret?state=full(value)&next=ssh";
        string text = "To authenticate, visit " + string.Format(wrapper, url) + " to continue.";

        Assert.IsTrue(TailscaleSshCheckUri.TryGetVerificationUri(text, out Uri? uri));
        Assert.IsNotNull(uri);
        Assert.AreEqual(url, uri.OriginalString);
    }

    [TestMethod]
    [DataRow("https://login.tailscale.com/a/secret?state=value(inner)")]
    [DataRow("https://login.tailscale.com/a/secret?state=original%23fragment&token=last")]
    public void URL内部标点与Token不能在打开前截断(string url)
    {
        Assert.IsTrue(TailscaleSshCheckUri.TryGetVerificationUri("Visit " + url + " now", out Uri? uri));
        Assert.IsNotNull(uri);
        Assert.AreEqual(url, uri.OriginalString);
    }

    [TestMethod]
    [DataRow("https://login.tailscale.com/a/sensitive-token?state=sensitive-query")]
    [DataRow("http://login.tailscale.com/a/sensitive-token?state=sensitive-query")]
    [DataRow("https://user@login.tailscale.com/a/sensitive-token?state=sensitive-query")]
    [DataRow("https://sub.login.tailscale.com/a/sensitive-token?state=sensitive-query")]
    [DataRow("https://login.tailscale.com.evil.example/a/sensitive-token?state=sensitive-query")]
    [DataRow("https://login.tailscale.com@evil.example/a/sensitive-token?state=sensitive-query")]
    [DataRow("https://login.tailscale.com:444/a/sensitive-token?state=sensitive-query")]
    [DataRow("https://LOGIN.TAILSCALE.COM/a/sensitive-token?state=sensitive-query#fragment")]
    [DataRow("https://login\u3002tailscale.com/a/sensitive-token?state=sensitive-query")]
    [DataRow("https://ｌｏｇｉｎ.ｔａｉｌｓｃａｌｅ.ｃｏｍ/a/sensitive-token?state=sensitive-query")]
    [DataRow("http://login\u3002tailscale.com/a/sensitive-token?state=sensitive-query")]
    [DataRow("https://sub.login\u3002tailscale.com/a/sensitive-token?state=sensitive-query")]
    [DataRow("https://ｌｏｇｉｎ.ｔａｉｌｓｃａｌｅ.ｃｏｍ.evil.example/a/sensitive-token?state=sensitive-query")]
    [DataRow("https://login.tailscale.com/a/sensitive-token\\suffix?state=sensitive-query")]
    [DataRow("https://login.tailscale.com/a/sensitive-token\u200Bsuffix?state=sensitive-query")]
    [DataRow("https://login.tailscale.com/a/sensitive-token\u0001suffix?state=sensitive-query")]
    public void 即使不允许打开的伪装候选也必须完整脱敏(string candidate)
    {
        string text = "Authorized use only. Visit (" + candidate + "). All activity is logged.";

        string redacted = TailscaleSshCheckUri.RedactVerificationUris(text);

        Assert.DoesNotContain("sensitive-token", redacted);
        Assert.DoesNotContain("sensitive-query", redacted);
        Assert.Contains("Authorized use only.", redacted);
        Assert.Contains("All activity is logged.", redacted);
    }

    [TestMethod]
    public void 同一文本的全部验证URL脱敏但普通URL保留()
    {
        const string ordinary = "https://example.com/help?topic=ssh";
        const string text = "Authorized use only. https://login.tailscale.com/a/first-secret?state=first-query "
            + "https://login.tailscale.com:444/a/second-secret?state=second-query "
            + ordinary + " All activity is logged.";

        string redacted = TailscaleSshCheckUri.RedactVerificationUris(text);

        Assert.DoesNotContain("first-secret", redacted);
        Assert.DoesNotContain("first-query", redacted);
        Assert.DoesNotContain("second-secret", redacted);
        Assert.DoesNotContain("second-query", redacted);
        Assert.Contains(ordinary, redacted);
        Assert.Contains("Authorized use only.", redacted);
        Assert.Contains("All activity is logged.", redacted);
    }

    [TestMethod]
    public void 无验证链接的普通文本保持原样()
    {
        const string text = "Authorized use only. https://example.com/a/public?state=ordinary https://login.tailscale.com/help";

        Assert.AreEqual(text, TailscaleSshCheckUri.RedactVerificationUris(text));
        Assert.IsFalse(TailscaleSshCheckUri.TryGetVerificationUri(text, out Uri? uri));
        Assert.IsNull(uri);
    }

    [TestMethod]
    public void 通知格式化不能泄漏验证链接()
    {
        Uri uri = new("https://login.tailscale.com/a/sensitive-token?state=sensitive-query");
        TailscaleSshCheckNotification notification = new(Guid.NewGuid(), "host:22", uri, () => { }, CancellationToken.None);

        string text = notification.ToString();

        Assert.DoesNotContain(uri.OriginalString, text);
        Assert.DoesNotContain("https://", text);
        Assert.DoesNotContain("sensitive-token", text);
        Assert.DoesNotContain("sensitive-query", text);
    }
}
