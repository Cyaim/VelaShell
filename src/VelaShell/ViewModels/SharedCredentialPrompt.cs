using VelaShell.Core.Credentials;
using VelaShell.Core.Models;

namespace VelaShell.ViewModels;

/// <summary>
/// 登录框与共享凭据(#550)之间的两头换算:弹框前说清为什么又来问,弹框后把输入落到哪里。
/// </summary>
/// <remarks>
/// 从窗口代码后置里拆出来,是为了不弹窗也能测:这几条规则错一条,
/// 要么用户改完密码还得一台台再输,要么一次手输把"跟着凭据走"的用户名悄悄钉死在了连接上。
/// </remarks>
public static class SharedCredentialPrompt
{
    /// <summary>
    /// 一条引用共享凭据的连接又来问凭据时,登录框该怎么说。
    /// </summary>
    /// <param name="profile">要连接的配置。</param>
    /// <param name="credential">它引用的共享凭据;已被删除时为 null。</param>
    /// <param name="usageCount">引用这条凭据的连接数。</param>
    /// <returns>登录框的共享凭据上下文。</returns>
    public static SharedCredentialPromptContext BuildContext(SessionProfile profile, SharedCredential? credential, int usageCount)
    {
        ArgumentNullException.ThrowIfNull(profile);
        SharedCredentialPromptReason reason;
        if (credential is null)
        {
            reason = SharedCredentialPromptReason.Missing;
        }
        else if (credential.AuthMethod == AuthMethod.Tailscale)
        {
            reason = SharedCredentialPromptReason.Unsupported;
        }
        else if (CredentialMaterial.HasInline(profile))
        {
            // 用户这次手输的又被拒了:原因用户自己清楚,不再复述。
            reason = SharedCredentialPromptReason.None;
        }
        else if (!credential.HasSecret
                 || (profile.ConnectionType != ConnectionType.Plugin
                     && string.IsNullOrWhiteSpace(profile.Username)
                     && string.IsNullOrWhiteSpace(credential.Username)))
        {
            reason = SharedCredentialPromptReason.Incomplete;
        }
        else
        {
            reason = SharedCredentialPromptReason.Rejected;
        }
        return new(credential?.Name ?? string.Empty, usageCount, reason);
    }

    /// <summary>登录框第一步要预填的用户名:连接自己的,没有就用凭据的。</summary>
    /// <param name="profile">要连接的配置。</param>
    /// <param name="credential">它引用的共享凭据;没有或已删除时为 null。</param>
    /// <returns>用户名。</returns>
    public static string InitialUsername(SessionProfile profile, SharedCredential? credential) =>
        profile.AuthMethod != AuthMethod.Tailscale && string.IsNullOrWhiteSpace(profile.Username)
            && credential is { AuthMethod: not AuthMethod.Tailscale }
            ? credential.Username
            : profile.Username;

    internal static AuthenticationDialogViewModel CreateDialog(
        SessionProfile profile, SharedCredential? credential, string? knownFingerprint,
        SharedCredentialPromptContext? sharedContext)
    {
        bool fromCredential = profile.AuthMethod != AuthMethod.Tailscale
                              && credential is { AuthMethod: not AuthMethod.Tailscale }
                              && !CredentialMaterial.HasInline(profile);
        var viewModel = new AuthenticationDialogViewModel(profile.Host, profile.Port,
            InitialUsername(profile, credential), knownFingerprint,
            fromCredential ? credential!.AuthMethod : profile.AuthMethod, sharedContext, profile.ConnectionType);
        if (fromCredential && credential!.AuthMethod is AuthMethod.PrivateKey or AuthMethod.Certificate)
        {
            viewModel.PrivateKeyPath = credential.PrivateKeyPath;
            viewModel.CertificatePath = credential.CertificatePath;
        }
        return viewModel;
    }

    /// <summary>
    /// 把登录框的输入落成这一次连接用的配置副本;勾了「同时更新共享凭据」时另给出要存回的凭据。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>返回副本,不改 <paramref name="profile" />。</b>传进来的可能是会话树缓存、命令面板缓存里那一份,
    /// 就地改的话,用户这次手输(没让记住)的密码会跟着拖动分组、复制配置之类的保存被落盘。
    /// </para>
    /// <para>
    /// 用户名的去处:连接一直跟着凭据走(自己没填)时,输入与凭据里的相同、或已经存回凭据,
    /// 就不往连接上写 —— 连上之后工作流会把这份配置存回去,写了就等于悄悄把"跟着凭据走"变成"覆盖"。
    /// </para>
    /// </remarks>
    /// <param name="profile">要连接的配置。</param>
    /// <param name="credential">它引用的共享凭据;没引用或已删除时为 null。</param>
    /// <param name="result">登录框的结果(不含明文密码)。</param>
    /// <param name="password">登录框里输入的明文密码(密码认证时)。</param>
    /// <param name="updatedCredential">要存回的共享凭据;不更新时为 null。</param>
    /// <returns>这一次连接用的配置副本。</returns>
    public static SessionProfile Apply(
        SessionProfile profile,
        SharedCredential? credential,
        AuthenticationResult result,
        string? password,
        out SharedCredential? updatedCredential)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(result);
        SessionProfile copy = profile.Clone();
        if (result.AuthMethod == AuthMethod.Tailscale)
        {
            copy.AuthMethod = AuthMethod.Tailscale;
            copy.Username = result.Username;
            CredentialMaterial.ClearTailscaleCredentials(copy);
            updatedCredential = null;
            return copy;
        }
        bool update = credential is { AuthMethod: not AuthMethod.Tailscale } && result.UpdateSharedCredential;
        bool inheritsUsername = credential is { AuthMethod: not AuthMethod.Tailscale }
                                && string.IsNullOrWhiteSpace(profile.Username);
        if (!inheritsUsername
            || !(update || string.Equals(result.Username, credential!.Username, StringComparison.Ordinal)))
        {
            copy.Username = result.Username;
        }
        copy.AuthMethod = result.AuthMethod;
        if (result.AuthMethod == AuthMethod.Password)
        {
            copy.Password = password;
            if (credential is null)
            {
                // 引用共享凭据的连接自己不存密码(仓储也会剥掉),这一勾对它没有意义。
                copy.RememberPassword = result.RememberPassword;
            }
        }
        else
        {
            copy.PrivateKeyPath = result.PrivateKeyPath;
            copy.PrivateKeyPassphrase = result.PrivateKeyPassphrase;
            // 证书路径无条件跟着写回,包括写回 null:从证书认证切回密钥认证时它必须被清掉,
            // 否则 AuthMethod 已经是 PrivateKey、证书路径却还留着上一次的值。
            copy.CertificatePath = result.CertificatePath;
        }

        updatedCredential = null;
        if (update)
        {
            SharedCredential updated = credential!.Clone();
            if (inheritsUsername)
            {
                updated.Username = result.Username;
            }
            bool keyBased = result.AuthMethod != AuthMethod.Password;
            updated.AuthMethod = result.AuthMethod;
            updated.Password = keyBased ? null : password;
            updated.PrivateKeyPath = keyBased ? result.PrivateKeyPath : null;
            updated.PrivateKeyPassphrase = keyBased ? result.PrivateKeyPassphrase : null;
            updated.CertificatePath = result.AuthMethod == AuthMethod.Certificate ? result.CertificatePath : null;
            updatedCredential = updated;
        }
        return copy;
    }
}
