using VelaShell.Core.Credentials;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Infrastructure.Credentials;

/// <summary>
/// <see cref="ICredentialResolver" /> 的实现:按引用里的来源 id 找到 <see cref="ICredentialProvider" />,
/// 取回认证材料,填进一份副本交给这一次连接(#550)。
/// </summary>
/// <remarks>
/// 完整性在这里按连接类型判,而不是交给来源:来源不知道这条凭据要拿去连什么 ——
/// 同一套私钥对 SSH 是完整的,对 FTP 却根本用不上。
/// </remarks>
public sealed class CredentialResolver(IEnumerable<ICredentialProvider> providers) : ICredentialResolver
{
    private readonly ICredentialProvider[] _providers = [.. providers ?? throw new ArgumentNullException(nameof(providers))];

    /// <inheritdoc />
    public async Task<SessionProfile> ResolveAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.AuthMethod == AuthMethod.Tailscale)
        {
            SessionProfile copy = profile.Clone();
            CredentialMaterial.ClearTailscaleCredentials(copy);
            return copy;
        }
        // 系统密钥库同样不走共享凭据:钥在这台电脑的密钥库里。万一带着引用(手改的配置),不去取它。
        if (profile.AuthMethod == AuthMethod.KeyStore && profile.CredentialSource is not null)
        {
            SessionProfile copy = profile.Clone();
            copy.CredentialSource = null;
            return copy;
        }
        // 本次在登录框里手输过的材料优先于引用(否则用户刚输的新密码会被引用里那份旧的盖掉)。
        // 但用户名可能仍要向引用去取:连接自己没填、一直跟着凭据走的那种 —— 登录框不会为了
        // 一次手输就把它钉在连接上(那会悄悄把"跟着凭据走"变成"覆盖")。
        bool inline = CredentialMaterial.HasInline(profile);
        if (profile.CredentialSource is not { } reference
            || (inline && !string.IsNullOrWhiteSpace(profile.Username))
            // 匿名 FTP 不发任何凭据:这时去取一条可能已被删掉的凭据,只会凭空多出一条失败。
            || profile is { ConnectionType: ConnectionType.FTP, Ftp.Anonymous: true })
        {
            return profile;
        }
        ICredentialProvider provider = Array.Find(_providers, p => p.Id == reference.ProviderId)
                                       ?? throw new CredentialProviderException(
                                           CredentialFailure.ProviderDisabled,
                                           reference.ProviderId,
                                           Strings.Format("Cred_Failure_ProviderDisabled", reference.ProviderId));
        ResolvedCredential resolved = await provider.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
        if (resolved.AuthMethod == AuthMethod.Tailscale)
        {
            throw new CredentialProviderException(
                CredentialFailure.UnsupportedAuthMethod,
                provider.Id,
                Strings.Get("SharedCred_ErrTailscaleUnsupported"));
        }

        // FTP 与插件协议只有「用户名 + 口令」这一种登录:把私钥交给它们,发出去的是一个空口令。
        if (!inline
            && profile.ConnectionType is ConnectionType.FTP or ConnectionType.Plugin
            && resolved.AuthMethod != AuthMethod.Password)
        {
            throw new CredentialProviderException(
                CredentialFailure.UnsupportedAuthMethod,
                provider.Id,
                Strings.Format("Cred_Failure_UnsupportedAuth", resolved.DisplayName));
        }

        SessionProfile effective = profile.Clone();
        // 连接自己填了用户名就以连接为准:同一套密钥常要配不同的登录名(ec2-user / ubuntu)。
        effective.Username = string.IsNullOrWhiteSpace(profile.Username)
            ? resolved.Username?.Trim() ?? string.Empty
            : profile.Username;
        if (!inline)
        {
            effective.AuthMethod = resolved.AuthMethod;
            effective.Password = resolved.Password;
            effective.PrivateKeyPath = resolved.PrivateKeyPath;
            effective.PrivateKeyPassphrase = resolved.PrivateKeyPassphrase;
            effective.CertificatePath = resolved.CertificatePath;
        }

        // 插件协议允许没有用户名(S3 匿名桶、没设 requirepass 的 Redis),其余都要。
        if (string.IsNullOrWhiteSpace(effective.Username) && profile.ConnectionType != ConnectionType.Plugin)
        {
            throw new CredentialProviderException(
                CredentialFailure.MissingUsername,
                provider.Id,
                Strings.Format("Cred_Failure_MissingUsername", resolved.DisplayName));
        }
        if (effective.AuthMethod != AuthMethod.Agent && !CredentialMaterial.HasInline(effective))
        {
            throw new CredentialProviderException(
                CredentialFailure.MissingSecret,
                provider.Id,
                Strings.Format("Cred_Failure_MissingSecret", resolved.DisplayName));
        }
        return effective;
    }
}
