using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Core.Credentials;

/// <summary>
/// 连接配置上那一组认证字段(密码、私钥、口令、证书)与凭据引用之间的规矩。
/// </summary>
/// <remarks>
/// <para>
/// 引用了凭据的配置,落盘时这组字段一律是空的(仓储层强制,见 <see cref="ClearInline" />)。
/// 所以内存里一条带引用的配置若这组字段**有值**,只可能来自这一次在登录框里的手输 ——
/// 凭据被服务器拒绝、或取不到时退回登录框,用户填的那一份。这时以手输为准(<see cref="HasInline" />),
/// 不再去查凭据,否则用户刚输的密码会被引用里那份旧的盖掉,重试永远在用同一个错密码。
/// </para>
/// <para>
/// 用户名不在这组字段里:引用凭据的配置照样可以有自己的用户名(覆盖凭据里的),
/// 它不代表"手输过"。
/// </para>
/// </remarks>
public static class CredentialMaterial
{
    /// <summary>配置上是否带着一套可以直接用的认证材料(对带引用的配置而言,即"本次手输过")。</summary>
    /// <param name="profile">连接配置。</param>
    /// <returns>带着时为 true。Agent 认证没有材料可带,恒为 false。</returns>
    public static bool HasInline(SessionProfile profile) => profile.AuthMethod switch
    {
        AuthMethod.Password => !string.IsNullOrEmpty(profile.Password),
        AuthMethod.PrivateKey => !string.IsNullOrWhiteSpace(profile.PrivateKeyPath),
        AuthMethod.Certificate => !string.IsNullOrWhiteSpace(profile.PrivateKeyPath)
                                  && !string.IsNullOrWhiteSpace(profile.CertificatePath),
        // 系统密钥库的「材料」就是那把钥的引用;它不走共享凭据,有引用就算带着。
        AuthMethod.KeyStore => profile.KeyStoreKey is not null,
        _ => false
    };

    /// <summary>
    /// 这条配置的凭据要在连接那一刻向引用去取:有引用,且没有本次手输的材料。
    /// </summary>
    /// <remarks>
    /// 「缺不缺凭据、要不要先弹登录框」的几处判定都以它为准:为 true 时不弹 ——
    /// 凭据由引用提供,取不到时连接流程会自己退回登录框。
    /// </remarks>
    /// <param name="profile">连接配置。</param>
    /// <returns>需要解析时为 true。</returns>
    public static bool NeedsResolution(SessionProfile profile) =>
        profile.AuthMethod is not (AuthMethod.Tailscale or AuthMethod.KeyStore) && profile.CredentialSource is not null && !HasInline(profile);

    /// <summary>
    /// 配置上这次要用的那份已存机密(密码认证的密码,私钥 / 证书认证的私钥口令)在这台设备上解不开 ——
    /// 载入时仍是密文原样(<see cref="StoredSecret" />)。这时不该拿它去登录,该直接问用户并说清原因。
    /// </summary>
    /// <param name="profile">连接配置(仓储载入的,或登录框交回的副本)。</param>
    /// <returns>解不开时为 true。</returns>
    public static bool HasUnreadableSecret(SessionProfile profile) =>
        UnreadableFor(profile.AuthMethod, profile.Password, profile.PrivateKeyPassphrase);

    /// <summary>共享凭据里那份已存机密在这台设备上解不开(口径同 <see cref="HasUnreadableSecret(SessionProfile)" />)。</summary>
    /// <param name="credential">共享凭据(仓储载入的)。</param>
    /// <returns>解不开时为 true。</returns>
    public static bool HasUnreadableSecret(SharedCredential credential) =>
        UnreadableFor(credential.AuthMethod, credential.Password, credential.PrivateKeyPassphrase);

    private static bool UnreadableFor(AuthMethod method, string? password, string? passphrase) => method switch
    {
        AuthMethod.Password => StoredSecret.IsUnreadable(password),
        AuthMethod.PrivateKey or AuthMethod.Certificate => StoredSecret.IsUnreadable(passphrase),
        _ => false
    };

    /// <summary>清掉配置上的认证材料(密码、私钥、口令、证书)。用户名与认证方式不动。</summary>
    /// <param name="profile">要清的配置(就地修改)。</param>
    public static void ClearInline(SessionProfile profile)
    {
        profile.Password = null;
        profile.PrivateKeyPath = null;
        profile.PrivateKeyPassphrase = null;
        profile.CertificatePath = null;
    }

    /// <summary>清掉 Tailscale SSH 不使用的认证材料与引用,保留远端用户名和其它连接设置。</summary>
    /// <param name="profile">要清的配置(就地修改)。其它认证方式不动。</param>
    public static void ClearTailscaleCredentials(SessionProfile profile)
    {
        if (profile.AuthMethod != AuthMethod.Tailscale)
        {
            return;
        }
        ClearInline(profile);
        profile.CredentialSource = null;
        profile.RememberPassword = false;
    }
}
