namespace VelaShell.Core.Ssh;

/// <summary>~/.ssh 下的一个密钥对。</summary>
/// <param name="Name">密钥名称(私钥文件名)。</param>
/// <param name="Type">密钥类型(如 RSA、ED25519)。</param>
/// <param name="Fingerprint">密钥指纹。</param>
/// <param name="PrivateKeyPath">私钥文件的完整路径。</param>
/// <param name="PublicKeyLine">OpenSSH 格式的公钥行;无公钥时为 null。</param>
public sealed record SshKeyInfo(
    string Name,
    string Type,
    string Fingerprint,
    string PrivateKeyPath,
    string? PublicKeyLine);

/// <summary>生成密钥时可选的算法。</summary>
public enum SshKeyAlgorithm
{
    /// <summary>
    /// Ed25519(默认)。OpenSSH 6.5(2014)起支持,固定 256 位强度、私钥只有 32 字节,
    /// 生成与签名都远快于 RSA。除非对端是十来年前的老 SSH 服务端,都该用这个。
    /// </summary>
    Ed25519,

    /// <summary>
    /// ECDSA(NIST P-256 / P-384 / P-521)。曲线由 <c>bits</c> 取 256 / 384 / 521 指定,默认 256。
    /// 比 RSA 短小,但用的是 NIST 曲线 —— 没有非它不可的理由时优先 Ed25519。
    /// </summary>
    Ecdsa,

    /// <summary>
    /// RSA。留给不认 Ed25519 的老服务端 —— 以及那些只把 <c>ssh-rsa</c> 写进白名单的堡垒机。
    /// 位数由 <c>bits</c> 指定,默认 4096;低于 2048 位请不要用。
    /// </summary>
    Rsa
}

/// <summary>导入一把私钥的结果。</summary>
/// <param name="Key">导入后的密钥(按真实解析结果)。</param>
/// <param name="NeedsPassphrase">
/// 私钥加了口令:格式对、结构完整,但要口令才解得开 —— 导入时不问口令,连接时再问。
/// 与「不是私钥」「私钥坏了」分开报,用户才知道该去找口令还是该换文件。
/// </param>
public sealed record SshKeyImportResult(SshKeyInfo Key, bool NeedsPassphrase);

/// <summary>SSH 密钥管理(设置 - 密钥管理页):枚举、导入、导出、生成、删除 ~/.ssh 下的密钥对。</summary>
public interface ISshKeyService
{
    /// <summary>枚举 ~/.ssh 下已有的密钥对。</summary>
    Task<List<SshKeyInfo>> ListKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 把外部私钥(及同名 .pub)复制进 ~/.ssh。先用 SSH 库把私钥完整解析一遍:坏的、不受支持的拒收,
    /// 加了口令的照收并标明;.pub 与私钥不是同一把时拒收。返回 null 表示同名文件已存在。
    /// </summary>
    /// <exception cref="FileNotFoundException">源私钥不存在,或没有 .pub 而私钥里又读不出公钥。</exception>
    /// <exception cref="InvalidDataException">不是私钥、私钥损坏或不受支持、.pub 不是公钥或与私钥不配对。</exception>
    Task<SshKeyImportResult?> ImportKeyAsync(string sourcePrivateKeyPath, CancellationToken cancellationToken = default);

    /// <summary>把一把密钥的公钥行写到 <paramref name="destinationPath" />(已存在则覆盖)。</summary>
    /// <param name="key">密钥。</param>
    /// <param name="destinationPath">目标文件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ExportPublicKeyAsync(SshKeyInfo key, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把一把密钥的私钥文件原样复制到 <paramref name="destinationPath" />(已存在则覆盖);
    /// Unix 上目标文件只给属主读写(0600),写入内容之前就收窄。
    /// </summary>
    /// <param name="key">密钥。</param>
    /// <param name="destinationPath">目标文件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="FileNotFoundException">私钥文件不在了。</exception>
    Task ExportPrivateKeyAsync(SshKeyInfo key, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 生成密钥对(OpenSSH 格式私钥 + OpenSSH 公钥行),默认 Ed25519。名称已存在时抛 IOException。
    /// </summary>
    /// <param name="name">密钥名称,即 ~/.ssh 下的私钥文件名。</param>
    /// <param name="algorithm">密钥算法,默认 <see cref="SshKeyAlgorithm.Ed25519" />。</param>
    /// <param name="bits">
    /// RSA 的模数位数,或 ECDSA 的曲线(256 / 384 / 521)。<b>0 表示按算法取默认值</b>
    /// (RSA 4096、ECDSA 256);Ed25519 是定长的,这个值一律忽略。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<SshKeyInfo> GenerateKeyAsync(
        string name,
        SshKeyAlgorithm algorithm = SshKeyAlgorithm.Ed25519,
        int bits = 0,
        CancellationToken cancellationToken = default);

    /// <summary>删除指定名称的密钥对(私钥及同名 .pub)。</summary>
    Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出本机 ssh-agent 里的密钥(连接配置里「只转发指定密钥」的候选)。
    /// </summary>
    /// <remarks>
    /// <see cref="SshKeyInfo.Name" /> 是 agent 给的注释,<see cref="SshKeyInfo.PrivateKeyPath" /> 为空串。
    /// agent 没在运行、连不上时返回空列表而不是抛出 —— 候选里还有 ~/.ssh 下的钥可选。
    /// </remarks>
    /// <param name="agentEndpoint">这条连接指定的 agent 端点(<c>SshSessionOptions.AgentEndpoint</c>);<see langword="null" /> = 系统默认。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<List<SshKeyInfo>> ListAgentKeysAsync(string? agentEndpoint = null, CancellationToken cancellationToken = default);
}
