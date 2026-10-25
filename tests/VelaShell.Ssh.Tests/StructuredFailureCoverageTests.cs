// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/08-failures.md §一（异常里装数据，不装拼好的句子）

namespace VelaShell.Ssh.Tests;

/// <summary>
/// 库里每一处 <c>new SshPrivateKeyException(…)</c> / <c>new SshCertificateException(…)</c> / <c>new SshChannelException(…)</c>，
/// 以及主机密钥被拒、代理 / 跳板拒绝的 <c>new SshConnectException(…)</c>，都要带上种类（<c>ProblemKind</c> / <c>FailureKind</c>）。
/// </summary>
/// <remarks>
/// 宿主按种类说界面语言（<c>SshInterop.Diagnostics.cs</c>）；新加的抛出点忘了归类，英 / 日 / 韩界面就又回到库的中文原文 ——
/// 而那种回退不会让任何用例变红，只有用户看得见。这条用例扫源码把它拦在提交之前。
/// 走得到的坏输入另有 <c>MalformedKeyInputTests</c> 按行为核对；这里管的是那些很难构造出来的抛出点。
/// </remarks>
[TestClass]
[TestCategory("Diagnostics")]
public sealed class StructuredFailureCoverageTests
{
    /// <summary>
    /// 要扫的构造与它必须带的种类属性。<c>When</c> 不为空时只管参数里提到其中之一的那些抛出点 ——
    /// 建连异常里 DNS、TCP、超时这些原因码本身就说得清，只有主机密钥被拒与代理 / 跳板拒绝才必须归类
    /// （代理握手的其余失败走 <c>ProxyDialing.Refused</c> / <c>Misconfigured</c>，种类是必填参数，编译期就拦住了）。
    /// </summary>
    private static readonly (string Constructor, string Property, string[]? When)[] Classified =
    [
        ("new SshPrivateKeyException(", "ProblemKind =", null),
        ("new SshCertificateException(", "ProblemKind =", null),
        ("new SshChannelException(", "FailureKind =", null),
        ("new SshForwardException(", "FailureKind =", null),
        ("new SshAgentException(", "FailureKind =", null),
        ("new SftpUnavailableException(", "FailureKind =", null),
        // 本库自己报的「服务端不支持」只在服务端缺扩展时出现：缺的是哪个要有结构化的出处。
        ("new SftpException(", "MissingExtensions =", ["OperationUnsupported"]),
        ("new SshConnectException(", "FailureKind =", ["HostKeyRejected", "HostKeyChanged", "ProxyRefused", "SshFailureReason.Unknown"]),
    ];

    [TestMethod]
    public void 库里抛出的私钥证书通道异常都带着种类()
    {
        string library = Path.Combine(RepositoryRoot(), "src", "VelaShell.Ssh");
        List<string> missing = [];
        int sites = 0;

        foreach (string file in Directory.EnumerateFiles(library, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach ((string constructor, string property, string[]? when) in Classified)
            {
                for (int at = text.IndexOf(constructor, StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf(constructor, at + constructor.Length, StringComparison.Ordinal))
                {
                    if (IsInComment(text, at))
                    {
                        continue;
                    }
                    int close = MatchParen(text, at + constructor.Length - 1);
                    if (when is not null
                        && !when.Any(word => text.AsSpan(at, close - at).Contains(word, StringComparison.Ordinal)))
                    {
                        continue;
                    }
                    sites++;
                    string after = text[(close + 1)..].TrimStart();
                    int end = after.StartsWith('{') ? after.IndexOf('}', StringComparison.Ordinal) : -1;
                    if (end < 0 || !after[..end].Contains(property, StringComparison.Ordinal))
                    {
                        int line = text.AsSpan(0, at).Count('\n') + 1;
                        missing.Add($"  {Path.GetRelativePath(library, file)}:{line} {constructor.TrimEnd('(')} 没有 {property.TrimEnd(' ', '=')}");
                    }
                }
            }
        }

        Assert.IsGreaterThanOrEqualTo(80, sites, $"只扫到 {sites} 处 —— 扫描八成失效了");
        Assert.IsEmpty(missing,
            "下面这些抛出点没有归类，宿主只能显示库的中文原文。给它一个种类（没有合适的就加一个，并在宿主 SshInterop 里配上五语文案）：\n" +
            string.Join("\n", missing));
    }

    /// <summary>这一处在 <c>//</c> 注释里（XML 文档注释也算）。</summary>
    private static bool IsInComment(string text, int at)
    {
        int lineStart = text.LastIndexOf('\n', at) + 1;
        return text.AsSpan(lineStart, at - lineStart).Contains("//", StringComparison.Ordinal);
    }

    /// <summary>与 <paramref name="open"/> 处的左括号配对的右括号；跳过字符串与内插串（含 <c>{…}</c> 里嵌套的字符串）。</summary>
    private static int MatchParen(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length;)
        {
            char c = s[i];
            if (c == '"' || (c == '$' && s[i + 1] == '"'))
            {
                i = SkipString(s, i);
                continue;
            }
            if (c == '\'')
            {
                i++;
                while (s[i] != '\'')
                {
                    i += s[i] == '\\' ? 2 : 1;
                }
                i++;
                continue;
            }
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }
            i++;
        }
        throw new InvalidOperationException("没配上右括号");
    }

    private static int SkipString(string s, int i)
    {
        bool interpolated = s[i] == '$';
        i += interpolated ? 2 : 1;
        while (true)
        {
            char c = s[i];
            if (c == '\\')
            {
                i += 2;
            }
            else if (c == '"')
            {
                return i + 1;
            }
            else if (interpolated && c == '{' && s[i + 1] == '{')
            {
                i += 2;
            }
            else if (interpolated && c == '{')
            {
                i++;
                for (int braces = 1; braces > 0;)
                {
                    char d = s[i];
                    if (d == '"' || (d == '$' && s[i + 1] == '"'))
                    {
                        i = SkipString(s, i);
                        continue;
                    }
                    braces += d == '{' ? 1 : d == '}' ? -1 : 0;
                    i++;
                }
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>从测试输出目录向上找到仓库根（以 VelaShell.slnx 为锚）。</summary>
    private static string RepositoryRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("找不到仓库根（VelaShell.slnx）。");
    }
}
