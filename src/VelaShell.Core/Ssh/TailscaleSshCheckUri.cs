using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VelaShell.Core.Resources;

namespace VelaShell.Core.Ssh;

/// <summary>官方 Tailscale SSH 验证链接的识别、允许规则与展示脱敏。</summary>
public static partial class TailscaleSshCheckUri
{
    /// <summary>仅取第一条完整且允许打开的官方验证链接。</summary>
    public static bool TryGetVerificationUri(string text, [NotNullWhen(true)] out Uri? uri)
    {
        foreach (Match match in UrlTokens().Matches(text))
        {
            string candidate = Unwrap(text, match);
            if (!EndsAtEmbeddedControl(text, match)
                && !HasForbiddenCharacters(candidate)
                && Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed)
                && IsAllowed(parsed))
            {
                uri = parsed;
                return true;
            }
        }
        uri = null;
        return false;
    }

    /// <summary>验证链接必须是官方 HTTPS 默认端口且不含可混淆的原始字符。</summary>
    public static bool IsAllowed(Uri uri)
    {
        try
        {
            return uri.IsAbsoluteUri
                && !HasForbiddenCharacters(uri.OriginalString)
                && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && uri.IdnHost.Equals("login.tailscale.com", StringComparison.OrdinalIgnoreCase)
                && uri.Port == 443
                && uri.UserInfo.Length == 0
                && uri.Fragment.Length == 0
                && uri.AbsolutePath.StartsWith("/a/", StringComparison.Ordinal)
                && uri.AbsolutePath.Length > 3;
        }
        catch (UriFormatException)
        {
            // Uri.TryCreate 可以接受尚未转换为 IDN 的主机名；不可转换的 authority 同样拒绝。
            return false;
        }
    }

    /// <summary>在任何普通展示缓存接收文本之前隐藏所有验证链接候选，包括伪装链接。</summary>
    public static string RedactVerificationUris(string text) => UrlTokens().Replace(text, match =>
        IsVerificationCandidate(match.Value)
            ? Strings.Get("Ssh_TailscaleCheckNotice")
            : match.Value);

    private static bool IsVerificationCandidate(string candidate)
    {
        if (!candidate.Contains("/a/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // 保留对伪域名、userinfo 和非法端口的宽脱敏；可解析主机再用与允许规则相同的 IDN 归一化。
        if (candidate.Contains("login.tailscale.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        try
        {
            return Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
                && uri.IdnHost.Contains("login.tailscale.com", StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool HasForbiddenCharacters(string value)
    {
        foreach (Rune character in value.EnumerateRunes())
        {
            if (character.Value == '\\' || Rune.IsControl(character)
                || Rune.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                return true;
            }
        }
        return false;
    }

    private static bool EndsAtEmbeddedControl(string text, Match match)
    {
        int end = match.Index + match.Length;
        // 换行是横幅自然边界；内嵌制表等控制分隔符不能把危险候选截成可打开的前缀。
        return end < text.Length && char.IsControl(text[end]) && text[end] is not ('\r' or '\n');
    }

    private static string Unwrap(string text, Match match)
    {
        string original = match.Value;
        ReadOnlySpan<char> value = original;
        // 只移除位于 URL 外面的配对括号，不截掉路径/query 本身的括号或其它后缀。
        int prefix = match.Index;
        while (prefix > 0 && text[prefix - 1] is '(' or '[' or '{')
        {
            prefix--;
        }
        for (; prefix < match.Index && !value.IsEmpty; prefix++)
        {
            char opening = text[prefix];
            char closing = opening switch { '(' => ')', '[' => ']', _ => '}' };
            if (value[^1] != closing || value.Count(closing) <= value.Count(opening))
            {
                break;
            }
            value = value[..^1];
        }
        return value.Length == original.Length ? original : value.ToString();
    }

    [GeneratedRegex("https?://[^\\s<>\"']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlTokens();
}
