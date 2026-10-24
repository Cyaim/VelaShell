namespace VelaShell.Core.Models;

/// <summary>信任库里一条带标记的记录是哪一种(OpenSSH <c>known_hosts</c> 的 <c>@revoked</c> / <c>@cert-authority</c>)。</summary>
/// <remarks>
/// 零值是 <see cref="Revoked" />:一条读坏了、种类没读出来的记录按「吊销」处理只会多拒绝一次连接,
/// 按「受信 CA」处理却会让这把钥签出的证书对模式匹配到的所有主机生效。
/// </remarks>
public enum KnownHostMarkerKind
{
    /// <summary><c>@revoked</c>:这把钥(或这张证书、这个 CA)已作废,对模式匹配到的主机一律拒绝。</summary>
    Revoked = 0,

    /// <summary><c>@cert-authority</c>:这把钥是受信的主机 CA,它签发的主机证书在主机名对上模式时直接受信。</summary>
    CertificateAuthority = 1,
}

/// <summary>
/// 信任库里一条按主机名模式生效的记录:受信的主机 CA,或吊销的钥。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="KnownHost" /> 不同,它不属于某一台 host:port,而是按 <see cref="HostPatterns" />
/// (<c>known_hosts</c> 那一栏的原文:逗号分隔,<c>*</c> / <c>?</c> 通配,<c>!</c> 取反,非 22 端口写成 <c>[host]:port</c>,
/// 也可以是 OpenSSH 散列过的 <c>|1|…</c>)管一批主机。匹配、证书验证都交给 SSH 库的 <c>KnownHostsFile.Lookup</c>,
/// 与它读 <c>known_hosts</c> 时同一套规则(velashell-docs/zh/ssh/spec/03 §5.4、§5.5)。
/// </para>
/// <para>存的是公钥 blob 本身(base64),不只是指纹:验证书要拿它去比签发 CA,导出要原样写回 <c>known_hosts</c>。</para>
/// </remarks>
public sealed class KnownHostMarker
{
    /// <summary>记录 Id(文档 Id)。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>受信 CA 还是吊销。</summary>
    public KnownHostMarkerKind Kind { get; set; }

    /// <summary>主机名模式,<c>known_hosts</c> 主机那一栏的原文(如 <c>*.corp.example,!legacy.corp.example</c>)。</summary>
    public string HostPatterns { get; set; } = string.Empty;

    /// <summary>公钥类型(blob 里的类型串,如 <c>ssh-ed25519</c>)。</summary>
    public string KeyType { get; set; } = string.Empty;

    /// <summary>公钥 blob 的 base64(<c>known_hosts</c> 第三栏)。</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>公钥的 SHA-256 指纹(<c>SHA256:…</c>),给人看。</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>注释(<c>known_hosts</c> 行尾的那段,或手动添加时填的说明);可以为空。</summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>加入信任库的时间(UTC)。</summary>
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
