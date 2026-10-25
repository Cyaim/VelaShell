using System.Text.RegularExpressions;

namespace VelaShell.Core.OutputRules;

/// <summary>
/// 自动应答规则的口令守卫:凡是会对密码 / 口令 / 验证码提示起反应的规则,都不许设成自动应答。
/// </summary>
/// <remarks>
/// <para>
/// 规则随设置保存、随云同步走,而自动应答发出去的是规则里的固定文本 —— 允许它答密码提示,
/// 就等于鼓励用户把口令写进这张表。要输口令的场景走共享密码(终端在密码提示处弹出的那张列表)。
/// </para>
/// <para>
/// 两道判据:正则字面上提到了口令一类的词;或者正则会命中一条常见的口令提示原文 ——
/// 后者拦的是 <c>:\s*$</c>、<c>.*</c> 这类字面上不带关键词、却会对密码提示应答的写法。
/// 宁可多拦:误拦的代价是用户换个更具体的写法,漏拦的代价是一条口令进了同步载荷。
/// 这里的中日韩字样是要匹配的远端输出原文,不是界面文案。
/// </para>
/// </remarks>
internal static class OutputRuleSecretGuard
{
    private static readonly Regex SecretWords = new(
        @"pass\s*(?:word|phrase|code)|passwd|\bpin\b|\botp\b|token|secret|verification\s+code|one[-\s]?time|密码|口令|验证码|密碼|驗證碼|パスワード|暗証|비밀번호|암호",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly string[] SecretPromptSamples =
    [
        "Password:",
        "password: ",
        "[sudo] password for alice: ",
        "[sudo: authenticate] Password: ",
        "alice@example.com's password: ",
        "Enter passphrase for key '/home/alice/.ssh/id_ed25519': ",
        "Enter PIN for 'PIV Card Holder pin': ",
        "Verification code: ",
        "Password for 'https://alice@example.com': ",
        "密码:",
        "[sudo] alice 的密码:",
        "パスワード:",
        "[sudo] alice のパスワード:",
        "암호:",
        "[sudo] alice의 암호:",
    ];

    /// <summary>这条规则会不会对口令提示起反应。</summary>
    /// <param name="pattern">规则的正则原文。</param>
    /// <param name="regex">编译好的正则(带上规则的大小写设定)。</param>
    /// <returns>会时为 true。</returns>
    public static bool LooksLikeSecretPrompt(string pattern, Regex regex)
    {
        if (SecretWords.IsMatch(pattern))
        {
            return true;
        }
        foreach (string sample in SecretPromptSamples)
        {
            try
            {
                foreach (ValueMatch match in regex.EnumerateMatches(sample))
                {
                    if (match.Length > 0)
                    {
                        return true;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return true; // 拿不准就当它会命中:这一侧宁可多拦。
            }
        }
        return false;
    }
}
