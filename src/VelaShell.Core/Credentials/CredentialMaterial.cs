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
        profile.AuthMethod != AuthMethod.Tailscale && profile.CredentialSource is not null && !HasInline(profile);

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
