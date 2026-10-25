using System.Text;

namespace VelaShell.Core.Ssh;

/// <summary>按连接指定的 agent 端点写得对不对。</summary>
public enum SshAgentEndpointProblem
{
    /// <summary>没问题(或没填,即系统默认)。</summary>
    None,

    /// <summary>写的是 Windows 命名管道(<c>\\.\pipe\…</c>),可这台机器不是 Windows。</summary>
    PipeNotSupported,

    /// <summary>只写了 <c>\\.\pipe\</c>,没有管道名。</summary>
    PipeNameMissing,

    /// <summary>套接字路径不是完整路径(相对路径落在哪全看程序的当前目录)。</summary>
    NotFullPath,

    /// <summary>套接字路径超过 <c>sun_path</c> 的上限。</summary>
    TooLong,
}

/// <summary>
/// 按连接指定的 ssh-agent 端点(<c>SshSessionOptions.AgentEndpoint</c>):Windows 命名管道 <c>\\.\pipe\名字</c>,或 Unix 域套接字的路径。
/// </summary>
/// <remarks>
/// <para>
/// 没填就是系统默认 —— 交给 SSH 库的 <c>SshAgentClient.DefaultEndpoint</c>:其它平台看 <c>SSH_AUTH_SOCK</c>;Windows 上 <c>SSH_AUTH_SOCK</c>
/// 是命名管道时用它,否则 OpenSSH agent 服务的管道,那个管道不在而开着 Pageant 时用 Pageant。按连接指定是给「这台机器只该用那几把钥」的人:
/// 1Password / KeePassXC / gpg-agent 各自的 agent、只装了部署钥的那个 agent(ssh_config 的 <c>IdentityAgent</c>)。
/// </para>
/// <para>
/// 存的是用户写的原样(去掉首尾空白),开头的 <c>~</c> 到连接那一刻才展开 —— 配置随云同步到别的机器上,主目录不一样。
/// Windows 上也认 Unix 套接字(Windows 10 1803 起有 AF_UNIX);Git Bash / WSL 的 agent 套接字是另一套东西,连不上。
/// </para>
/// </remarks>
public static class SshAgentEndpoints
{
    /// <summary>Windows 命名管道的前缀。</summary>
    public const string PipePrefix = @"\\.\pipe\";

    /// <summary>没填(<see langword="null" /> 或空白)= 系统默认。</summary>
    /// <param name="value">配置里存的值。</param>
    public static bool IsDefault(string? value) => string.IsNullOrWhiteSpace(value);

    /// <summary>存进配置的写法:去掉首尾空白与一层引号;空了存 <see langword="null" />(系统默认)。</summary>
    /// <param name="value">界面或 ssh_config 里写的值。</param>
    public static string? Normalize(string? value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1].Trim();
        }
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>是不是 Windows 命名管道的写法(前缀大小写不论)。</summary>
    /// <param name="value">端点。</param>
    public static bool IsPipe(string? value) =>
        Normalize(value) is { } text && text.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>连接时交给库的端点:命名管道前缀统一成库认的写法,开头的 <c>~</c> 展开成主目录;没填为 <see langword="null" />(库按系统默认)。</summary>
    /// <param name="value">配置里存的值。</param>
    /// <param name="homeDirectory">主目录;<see langword="null" /> 取当前用户的。</param>
    public static string? Resolve(string? value, string? homeDirectory = null)
    {
        if (Normalize(value) is not { } text)
        {
            return null;
        }
        if (text.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase))
        {
            // 库按区分大小写的前缀判断是不是管道(Windows 上管道名本身不分大小写)。
            return PipePrefix + text[PipePrefix.Length..];
        }
        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith(@"~\", StringComparison.Ordinal))
        {
            string home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return text.Length <= 2 ? home : Path.Combine(home, text[2..]);
        }
        return text;
    }

    /// <summary>本机套接字路径的上限(UTF-8 字节,不含结尾的 0):<c>sun_path</c> 在 Linux 与 Windows 上 108 字节、macOS 上 104 字节。</summary>
    /// <param name="macOS">是不是 macOS。</param>
    public static int MaxSocketPathBytes(bool macOS) => macOS ? 103 : 107;

    /// <summary>检查端点在这台机器上用不用得了;没填(系统默认)总是 <see cref="SshAgentEndpointProblem.None" />。</summary>
    /// <param name="value">配置里存的值。</param>
    /// <param name="windows">是不是 Windows(测试两种平台都能覆盖)。</param>
    /// <param name="macOS">是不是 macOS。</param>
    /// <param name="homeDirectory">主目录(展开 <c>~</c>)。</param>
    public static SshAgentEndpointProblem Check(string? value, bool windows, bool macOS, string homeDirectory)
    {
        if (Normalize(value) is not { } text)
        {
            return SshAgentEndpointProblem.None;
        }
        if (text.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!windows)
            {
                return SshAgentEndpointProblem.PipeNotSupported;
            }
            return text.Length > PipePrefix.Length && text.IndexOfAny(['\\', '/'], PipePrefix.Length) < 0
                ? SshAgentEndpointProblem.None
                : SshAgentEndpointProblem.PipeNameMissing;
        }
        string path = Resolve(text, homeDirectory)!;
        if (!IsFullyQualified(path, windows))
        {
            return SshAgentEndpointProblem.NotFullPath;
        }
        return Encoding.UTF8.GetByteCount(path) > MaxSocketPathBytes(macOS)
            ? SshAgentEndpointProblem.TooLong
            : SshAgentEndpointProblem.None;
    }

    /// <summary>按这台机器的规则检查(<see cref="Check(string?, bool, bool, string)" />)。</summary>
    /// <param name="value">配置里存的值。</param>
    public static SshAgentEndpointProblem Check(string? value) =>
        Check(value, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// 完整路径:Windows 上要盘符加分隔符(<c>C:\</c>;AF_UNIX 不认 UNC 路径),其它平台要 <c>/</c> 开头。
    /// 按参数里的平台判断,而不是 <see cref="Path.IsPathFullyQualified(string)" /> 按运行时的平台 —— 测试要两种都能覆盖。
    /// </summary>
    private static bool IsFullyQualified(string path, bool windows) =>
        windows
            ? path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
            : path.StartsWith('/');
}
