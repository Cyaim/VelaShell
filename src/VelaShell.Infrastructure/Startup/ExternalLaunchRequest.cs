using System.Text.Json.Serialization;
using VelaShell.Core.Models;
using VelaShell.Core.Net;

namespace VelaShell.Infrastructure.Startup;

/// <summary>外部拉起请求的类型。</summary>
public enum ExternalLaunchKind
{
    /// <summary>只是把已在运行的窗口唤到前台(第二次双击图标、托盘隐藏后再启动)。</summary>
    Activate,

    /// <summary>带目标(可能还带一次性凭据)的连接请求。</summary>
    Connect
}

/// <summary>请求是怎么进来的;确认弹窗要把它原样告诉用户。</summary>
public enum ExternalLaunchOrigin
{
    /// <summary><c>-url</c> / <c>-l</c> / <c>-p</c> 等 Xshell 风格命令行参数。</summary>
    CommandLine,

    /// <summary>系统 URL 协议(<c>ssh://</c> / <c>sftp://</c>),即网页里点一下就唤起本应用。</summary>
    UrlProtocol,

    /// <summary><c>-f &lt;session.xsh&gt;</c> 指向的 Xshell 会话文件。</summary>
    SessionFile
}

/// <summary>
/// 一次「从进程外拉起 VelaShell 去连某台服务器」的请求。Xshell 兼容登录的唯一数据载体:
/// 命令行、URL 协议、<c>.xsh</c> 会话文件三条入口都归一到它,单实例转发也只传它。
/// <para>
/// <see cref="Password" /> 是一次性凭据(堡垒机/SSO 现发的),**从不落盘**:
/// 由此建出的 <see cref="SessionProfile" /> 一律 <c>RememberPassword = false</c>
/// 且不写入会话仓储;<see cref="ToString" /> 也刻意不含密码,免得随手一句日志就把它抄进文件。
/// </para>
/// </summary>
public sealed class ExternalLaunchRequest
{
    /// <summary>请求类型;默认是连接请求。</summary>
    public ExternalLaunchKind Kind { get; init; } = ExternalLaunchKind.Connect;

    /// <summary>原始 scheme(小写:ssh / sftp / ftp / ftps / telnet …),确认弹窗按它显示协议名。</summary>
    public string Scheme { get; init; } = "ssh";

    /// <summary>映射后的连接类型;<see cref="IsSupported" /> 为 false 时该值无意义。</summary>
    public ConnectionType ConnectionType { get; init; } = ConnectionType.SSH;

    /// <summary>本应用是否支持这个 scheme(telnet / rlogin / serial 等一律 false,给出可读提示而不是静默丢弃)。</summary>
    public bool IsSupported { get; init; } = true;

    /// <summary>目标主机(主机名或 IP;IPv6 已去掉方括号)。</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>目标端口;URL 未给出时按 scheme 取默认值。</summary>
    public int Port { get; init; } = 22;

    /// <summary>登录用户名;缺省为空(交给应用内的登录弹窗问)。</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>随请求传来的一次性密码;没有则为 null。绝不持久化。</summary>
    public string? Password { get; init; }

    /// <summary>随请求指定的私钥文件(Xshell <c>-i</c>);没有则为 null。</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>
    /// 随请求指定的 OpenSSH 用户证书(PuTTY 的 <c>-cert</c>,与 <c>-i</c> 的私钥配对);没有则为 null。
    /// 有它就按证书认证连,私钥是 <see cref="PrivateKeyPath" />(没给 <c>-i</c> 时按 <c>&lt;私钥&gt;-cert.pub</c> 的命名约定反推)。
    /// </summary>
    public string? CertificatePath { get; init; }

    /// <summary>请求带没带登录材料(一次性密码、私钥或证书)。</summary>
    [JsonIgnore]
    public bool HasCredentials => Password is not null || PrivateKeyPath is not null || CertificatePath is not null;

    /// <summary>请求来源。</summary>
    public ExternalLaunchOrigin Origin { get; init; } = ExternalLaunchOrigin.CommandLine;

    /// <summary>
    /// 调用方指定的标签页标题(Xshell <c>-newtab &lt;会话名&gt;</c>,#475);没给则为 null,标签照常按配置起名。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 堡垒机客户端拿它说明「这是哪一台」:JumpServer 发的是 <c>root@Linux[2026_09_20_09_45_18]</c>,
    /// 某 SSO 客户端发的是 <c>CBH_10.1.2.3_22_root</c> —— 真正的目标是堡垒机自己的地址和代理端口,
    /// 没有这个名字,开出来的一排标签全叫同一个堡垒机地址,分不清谁是谁。
    /// </para>
    /// <para>
    /// 它只管标签上写什么,不参与信任判据(<see cref="TrustKey" />)与目标解析;状态栏照旧显示配置的名称,
    /// 那一行说的是「真连到了哪」。来自进程外,用之前一律过一遍 <see cref="NormalizeTabName" />。
    /// </para>
    /// </remarks>
    public string? TabName { get; init; }

    /// <summary>标签名的长度上限(按 UTF-16 码元)。标签条本来就会截断,这里只防一条把 64 KB 载荷塞满的名字。</summary>
    public const int MaxTabNameLength = 128;

    /// <summary>
    /// 把调用方给的标签名收拾成能直接上标签的样子:去掉首尾空白与引号、控制字符换成空格并合并连续空白、
    /// 超长截断(不在代理对中间切);剩下空串时返回 null。
    /// </summary>
    /// <remarks>
    /// 控制字符不能原样留着:换行会把标签撑成两行,ESC 一类的字节画出来是方框;
    /// 名字是别的程序给的,不能指望它干净。单实例转发的另一端收到后同样再过一遍 —— 管道那头的
    /// 也可能是一个手搓的客户端。
    /// </remarks>
    /// <param name="raw">原始标签名。</param>
    /// <returns>规整后的标签名,或 null。</returns>
    public static string? NormalizeTabName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var builder = new System.Text.StringBuilder(Math.Min(raw.Length, MaxTabNameLength));
        bool pendingSpace = false;
        foreach (char c in raw.Trim().Trim('"'))
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(c);
        }
        if (builder.Length > MaxTabNameLength)
        {
            int cut = char.IsHighSurrogate(builder[MaxTabNameLength - 1]) ? MaxTabNameLength - 1 : MaxTabNameLength;
            builder.Length = cut;
        }
        string normalized = builder.ToString().Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>
    /// 信任判据:scheme + 用户名 + 主机 + 端口。用户勾了「不再询问」记的就是它 ——
    /// 粒度必须含用户名与端口,否则「信任 10.0.3.21」会连带放行 <c>root@10.0.3.21</c>。
    /// </summary>
    [JsonIgnore]
    public string TrustKey =>
        $"{Scheme.ToLowerInvariant()}://{Username}@{Host.ToLowerInvariant()}:{Port}";

    /// <summary>给人看的目标(<c>user@host:port</c>,IPv6 加方括号);没有用户名时省掉前缀。</summary>
    [JsonIgnore]
    public string DisplayTarget => HostAddress.FormatTarget(Username, Host, Port);

    /// <summary>诊断用的字符串。**刻意不含密码**:这条随时可能被 Trace 抄进日志文件。</summary>
    public override string ToString() =>
        $"{Kind} {Scheme}://{DisplayTarget} (origin={Origin}, credentials={(Password is null ? "none" : "supplied")})";
}
