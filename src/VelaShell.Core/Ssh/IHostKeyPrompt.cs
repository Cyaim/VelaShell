namespace VelaShell.Core.Ssh;

/// <summary>主机指纹人工确认的用户裁决(与主流 SSH 客户端一致的三选项)。</summary>
public enum HostKeyDecision
{
    /// <summary>取消:拒绝该指纹并中止连接。</summary>
    Reject = 0,

    /// <summary>仅本次信任:放行连接但不写入已信任主机,本次应用运行内有效。</summary>
    TrustOnce = 1,

    /// <summary>永久信任:放行连接并写入已信任主机(known_hosts)。</summary>
    TrustPermanently = 2
}

/// <summary>
/// 主机指纹的人工确认入口(设置 → 安全审计 → 主机信任策略)。由 UI 层实现
/// (App 弹 HostKeyPromptView);在 SSH 握手线程上被同步等待。
/// </summary>
public interface IHostKeyPrompt
{
    /// <summary>询问用户如何处置该指纹;实现方失败时必须返回 <see cref="HostKeyDecision.Reject" />(fail-closed)。</summary>
    /// <param name="host">目标主机地址(主机名或 IP)。</param>
    /// <param name="port">目标主机的 SSH 端口。</param>
    /// <param name="keyType">本次服务端主机密钥的算法类型。</param>
    /// <param name="fingerprint">本次服务端主机密钥的指纹。</param>
    /// <param name="verification">本次指纹相对 known_hosts 的校验结果。</param>
    /// <param name="knownFingerprint">
    /// known_hosts 里已记录的旧指纹;首次连接(无记录)时为 <see langword="null" />。
    /// 指纹变更时必须摆出来 —— 用户要靠"原来是哪一把"才能判断这是自己重装的那台还是一次劫持。
    /// </param>
    /// <param name="randomArt">
    /// 本次主机密钥的指纹图(OpenSSH <c>ssh-keygen -lv</c> 那种 17×9 的图,行间 <c>\n</c>,要等宽字体);
    /// 人眼比对一张图比比对 43 个字符可靠得多。没有时为 <see langword="null" />。
    /// </param>
    /// <param name="certificateAuthority">
    /// 服务端出示的是主机证书、而信任库里没有为这台主机配受信 CA 时,签发它的 CA 公钥的 SHA-256 指纹;
    /// 否则为 <see langword="null" />。这时证书得不到任何额外的保证,问的仍是证书里那把钥 ——
    /// 摆出 CA 是为了让用户知道:核对之后把它加进受信主机 CA,同一个 CA 签的主机就不必逐台确认。
    /// </param>
    Task<HostKeyDecision> DecideAsync(string host, int port, string keyType, string fingerprint,
        HostKeyVerification verification, string? knownFingerprint = null, string? randomArt = null,
        string? certificateAuthority = null);
}
