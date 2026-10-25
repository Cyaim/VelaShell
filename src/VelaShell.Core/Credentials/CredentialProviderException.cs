namespace VelaShell.Core.Credentials;

/// <summary>凭据取不到或用不了的原因。</summary>
public enum CredentialFailure
{
    /// <summary>引用指向的来源在本机不可用(没注册,或比本机版本新的客户端同步过来的来源)。</summary>
    ProviderDisabled,

    /// <summary>条目不存在(被删了,或同步只带来了连接没带来凭据)。</summary>
    NotFound,

    /// <summary>凭据与连接配置都没有用户名。</summary>
    MissingUsername,

    /// <summary>凭据缺认证材料(密码 / 私钥 / 证书);云同步没开端到端口令时拉到的凭据就是这样。</summary>
    MissingSecret,

    /// <summary>认证方式不适用于这种连接(FTP 与插件协议只能用密码)。</summary>
    UnsupportedAuthMethod,

    /// <summary>
    /// 已存的密码 / 私钥口令在这台设备上解不开(本机加密密钥换过、系统密钥链这次没打开),
    /// 没有拿密文去试登录(<see cref="CredentialMaterial.HasUnreadableSecret(Models.SessionProfile)" />)。
    /// </summary>
    UnreadableSecret
}

/// <summary>
/// 凭据解析失败。<see cref="Exception.Message" /> 是面向用户的本地化文案,可直接显示。
/// </summary>
/// <remarks>
/// 连接流程见到它就退回登录框让用户手输 —— 解析失败绝不能变成"带着空密码去试一次",
/// 那只会多换来一次服务器端的失败登录记录。
/// </remarks>
/// <param name="kind">失败原因。</param>
/// <param name="providerId">凭据来源 id。</param>
/// <param name="message">面向用户的本地化文案。</param>
/// <param name="innerException">内部异常。</param>
public sealed class CredentialProviderException(
    CredentialFailure kind,
    string providerId,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>失败原因。</summary>
    public CredentialFailure Kind { get; } = kind;

    /// <summary>凭据来源 id。</summary>
    public string ProviderId { get; } = providerId;
}
