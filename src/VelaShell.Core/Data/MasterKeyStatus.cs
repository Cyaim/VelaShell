namespace VelaShell.Core.Data;

/// <summary>本机加密密钥此刻由谁保管(决定拿到数据目录的人能不能解开已存的密码)。</summary>
public enum MasterKeyProtection
{
    /// <summary>
    /// 系统密钥链保管(macOS 钥匙串 / Secret Service):数据目录里只剩条目编号,拷走数据目录解不开。
    /// </summary>
    SystemKeychain,

    /// <summary>
    /// 数据目录里的密钥文件经 DPAPI(当前 Windows 用户)包裹:换账户、换电脑解不开。
    /// </summary>
    UserBoundFile,

    /// <summary>
    /// 数据目录里的明文密钥文件,只靠 0600 权限 —— 系统密钥链用不了时的回落(与引入密钥链之前相同)。
    /// </summary>
    PlainFile
}

/// <summary>
/// 启动时解析本机加密密钥的结果:用的是哪种保管方式,以及有没有哪把旧密钥这次没取到。
/// </summary>
/// <remarks>
/// 抽象的目的是抹平调用方,不是抹平事实 —— 回落到文件、某把密钥取不到,都要如实交给界面去说,
/// 不能让用户以为一切照旧。只含编号与原因,不含任何密钥材料。
/// </remarks>
public sealed class MasterKeyStatus
{
    /// <summary>当前加密用的那把密钥由谁保管。</summary>
    public required MasterKeyProtection Protection { get; init; }

    /// <summary>这个平台的系统密钥链后端名;没有(Windows)时为 null。</summary>
    public string? KeychainName { get; init; }

    /// <summary>系统密钥链这次用不了的原因;用得了或没有密钥链时为 null。</summary>
    public string? KeychainError { get; init; }

    /// <summary>
    /// 数据目录记着、但这次因为密钥链用不了而没取到的密钥编号。它们没有丢:密钥链恢复后下次启动照常取回。
    /// </summary>
    public IReadOnlyList<string> UnavailableKeyIds { get; init; } = [];

    /// <summary>
    /// 数据目录记着、但密钥链里没有这一条的密钥编号(数据目录从别的电脑 / 账户拷来,或者条目被删了)。
    /// </summary>
    public IReadOnlyList<string> MissingKeyIds { get; init; } = [];

    /// <summary>
    /// 密钥文件在、却用不了(DPAPI 解不开、长度不对)时,它被原样改名保留成的文件名;没有这回事时为 null。
    /// </summary>
    public string? ReplacedKeyFile { get; init; }

    /// <summary>这次启动把数据目录里的明文密钥文件迁进了系统密钥链。</summary>
    public bool Migrated { get; init; }

    /// <summary>有已存的密码这次可能解不开(某把密钥没取到,或者密钥文件被换过)。</summary>
    public bool HasUnreadableSecrets =>
        UnavailableKeyIds.Count > 0 || MissingKeyIds.Count > 0 || ReplacedKeyFile is not null;
}

/// <summary>交出本机加密密钥的解析结果(首次调用时完成解析,可能阻塞,别在 UI 线程上调)。</summary>
public interface IMasterKeyStatusSource
{
    /// <summary>解析本机加密密钥(只做一次)并返回结果。</summary>
    MasterKeyStatus GetKeyStatus();
}
