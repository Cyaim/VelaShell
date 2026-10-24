using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Persistence;
using System.Globalization;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 把宿主的主机信任策略(<see cref="IHostKeyService" /> + <see cref="IHostKeyPrompt" /> +
/// 安全设置)接到 SSH 库的 <see cref="IHostKeyPolicy" /> 上。
/// </summary>
/// <remarks>
/// <para>
/// 链上**每一跳共用同一个实例** —— 跳板与终点走同一套策略。
/// 这一条以前是另写的一段:未知 / 变更一律返回 false,既不弹窗也不说原因,
/// 于是「只当跳板、从没单独连过的机器」第一次用就连不上。
/// </para>
/// <para>
/// <b>拒绝原因直接随裁决返回</b>(<see cref="SshHostKeyVerdict.Reject" />),
/// 不再需要 <c>HostKeyPromptOutcome</c> 那块「原因牌」—— 上一版底层库的回调只能返回
/// true/false,原因得靠一个旁路对象递出去,再由包装器在失败时读回来。
/// </para>
/// <para>
/// <b>弹窗的时间不计入连接超时</b>(库的 <c>HostKeyDecisionTimeout</c> 默认无限),
/// 所以那段「用户点了信任、这一轮却已被判超时,于是原地补连一次」的补偿逻辑
/// 也一并不存在了。
/// </para>
/// </remarks>
/// <param name="hostKey">已知主机记录。</param>
/// <param name="settings">安全设置(首次是否确认、变更是否阻断)。</param>
/// <param name="prompt">指纹确认弹窗;为 <see langword="null" /> 时走不问的两条默认。</param>
/// <param name="alerts">安全告警。</param>
/// <param name="automaticReconnect">
/// 这是断线后的自动重连(<see cref="ConnectionInfo.IsAutomaticReconnect" />):本该弹窗问的一律拒绝,不弹。
/// </param>
/// <param name="timeProvider">验主机证书有效期用的时钟;<see langword="null" /> 用系统时钟。</param>
internal sealed class VelaHostKeyPolicy(
    IHostKeyService hostKey,
    ISettingsService? settings,
    IHostKeyPrompt? prompt,
    ISecurityAlertService? alerts,
    bool automaticReconnect = false,
    TimeProvider? timeProvider = null) : IHostKeyPolicy, IHostKeyTypePreference, IHostKeyRotationPolicy
{
    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 交出这台主机记着的钥的类型(最近见过的在前),库会把它们的算法排到最前:协商以客户端的顺序为准,
    /// 正常的服务端因此谈成已记下的那一种。信任库按类型分开记(API-H4),接受过的几种都在这里。
    /// </para>
    /// <para>
    /// 曾经没有实现它。服务端新增一把 Ed25519 钥(或系统升级后自动生成了一把)时,
    /// 库默认把 Ed25519 排在前面,谈成的类型一变,而信任库按 host:port 只记一把钥,
    /// 就报「主机指纹已变更」—— 开着「变更即阻断」时连接直接失败;用户学会无视这个告警之后,
    /// 真有中间人时它也就没用了。
    /// </para>
    /// <para>
    /// 有对上这台主机的受信 CA 时再交出证书类型(库按 <c>@cert-authority</c> 报出它验得了的全部证书类型),
    /// 证书算法因此排到前面,服务端才会出示证书 —— 默认清单里证书排在普通算法之后,不这样就永远谈不成证书
    /// (velashell-docs/zh/ssh/spec/03 §5.5)。这台主机的普通密钥也记着时,普通算法仍在前:那把钥已经被明确信任,
    /// 用哪条路径验结果一样(库只调顺序,保持原清单里普通在前、证书在后的相对次序)。
    /// </para>
    /// <para>读不到记录时返回空、不阻断建连:裁决那一步还会再查一次。</para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> GetKnownKeyTypesAsync(
        string host, int port, CancellationToken cancellationToken = default)
    {
        List<string> types = [];
        try
        {
            IReadOnlyList<KnownHost> known = await hostKey.FindKnownHostKeysAsync(host, port, cancellationToken).ConfigureAwait(false);
            types.AddRange(known.Select(entry => entry.KeyType).Where(type => !string.IsNullOrEmpty(type)).Distinct(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.WriteLine($"[VelaShell] known_hosts lookup failed: {ex}");
        }

        try
        {
            IReadOnlyList<KnownHostEntry> markers = await LoadMarkerEntriesAsync(cancellationToken).ConfigureAwait(false);
            foreach (string type in KnownHostsFile.KnownKeyTypes(markers, host, port))
            {
                if (!types.Contains(type, StringComparer.Ordinal))
                {
                    types.Add(type);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.WriteLine($"[VelaShell] host CA lookup failed: {ex}");
        }
        return types;
    }

    /// <summary>信任库里的受信主机 CA 与吊销记录,换成库的条目。读不出来照实抛出(调用方决定是否 fail-closed)。</summary>
    private async Task<IReadOnlyList<KnownHostEntry>> LoadMarkerEntriesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<KnownHostMarker>? markers = await hostKey.GetMarkersAsync(cancellationToken).ConfigureAwait(false);
        return markers is null || markers.Count == 0 ? [] : KnownHostMarkers.ToEntries(markers);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 开着(OpenSSH 的 <c>UpdateHostKeys</c> 默认也开):服务端证明持有的新主机密钥补记进信任库,不再出示的旧钥删掉(Q4),
    /// 运维给服务器加一把 Ed25519、或者轮换掉老的 RSA 钥之后,用户不会看到「指纹已变更」。
    /// 只在这次用的钥已经永久信任过时才做(库里核对),「仅本次信任」的不算。信任库按类型分开记(API-H4),补记的就是新类型的那几条。
    /// </remarks>
    public bool AllowHostKeyUpdates => true;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> GetKnownHostKeyFingerprintsAsync(
        string host, int port, CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<KnownHost> known = await hostKey.FindKnownHostKeysAsync(host, port, cancellationToken).ConfigureAwait(false);
            return [.. known.Select(entry => entry.Fingerprint).Where(fingerprint => !string.IsNullOrEmpty(fingerprint))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.WriteLine($"[VelaShell] known_hosts lookup failed: {ex}");
            return [];
        }
    }

    /// <inheritdoc />
    /// <remarks>补记一把就告诉用户一声(安全告警),与 OpenSSH 的「Learned new hostkey」同一个意思。</remarks>
    public async ValueTask RecordHostKeysAsync(
        string host, int port, IReadOnlyList<SshPublicKey> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        string target = port == 22 ? host : HostAddress.FormatEndPoint(host, port);
        foreach (SshPublicKey key in keys)
        {
            await hostKey.TrustHostKeyAsync(host, port, key.PlainKeyType, key.Sha256Fingerprint, PublicKeyOf(key), cancellationToken).ConfigureAwait(false);
            if (alerts is not null)
            {
                await alerts.RaiseAsync("hostkey-learned",
                    Strings.Format("KeySvc_AlertHostKeyLearned", target, $"{key.PlainKeyType} {key.Sha256Fingerprint}")).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 〔Q4〕服务端不再出示的旧钥从信任库删掉(OpenSSH 的 <c>UpdateHostKeys</c> 也删),删一把告诉用户一声。
    /// 信任库按类型记、按类型删:一种类型的记录全都在要忘掉的指纹里才删那一种 —— 这期间又信任了同类型的别的钥,就不动。
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> ForgetHostKeysAsync(
        string host, int port, IReadOnlyList<string> fingerprints, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);
        string target = port == 22 ? host : HostAddress.FormatEndPoint(host, port);
        IReadOnlyList<KnownHost> known = await hostKey.FindKnownHostKeysAsync(host, port, cancellationToken).ConfigureAwait(false);
        List<string> forgotten = [];
        foreach (IGrouping<string, KnownHost> type in known.GroupBy(entry => entry.KeyType, StringComparer.Ordinal))
        {
            if (!type.All(entry => fingerprints.Any(fingerprint => SonnetDbHostKeyService.SameFingerprint(entry.Fingerprint, fingerprint))))
            {
                continue;
            }

            await hostKey.RemoveKnownHostAsync(host, port, type.Key, cancellationToken).ConfigureAwait(false);
            foreach (KnownHost entry in type)
            {
                forgotten.Add(entry.Fingerprint);
                if (alerts is not null)
                {
                    await alerts.RaiseAsync("hostkey-forgotten",
                        Strings.Format("KeySvc_AlertHostKeyForgotten", target, $"{type.Key} {entry.Fingerprint}")).ConfigureAwait(false);
                }
            }
        }
        return forgotten;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 三条出路:已信任直接放行、按设置弹窗裁决、fail-closed 拒绝。
    /// <para>
    /// **指纹变更默认走弹窗而不是直接拒**(#476)。「换了台服务器、IP 没变」是运维里
    /// 最常见的正当变更,旧行为把它当成一条连接错误抛出来,用户看到的只有一句英文的
    /// <c>UntrustedPeer</c>,唯一的出路是去 <c>.velashell</c> 里手动删记录 ——
    /// 而其它 SSH 客户端在这里弹的是一个「接受并覆盖 / 拒绝」的框。
    /// 要严格 fail-closed 的,把「指纹变更时阻断并告警」打开即可。
    /// </para>
    /// <para>
    /// <b>受信主机 CA 与吊销</b>(信任库的 <see cref="KnownHostMarker" />,velashell-docs/zh/ssh/spec/03 §5.5 的裁决顺序):
    /// ① 这把钥、这张证书或签发它的 CA 被吊销 → 拒绝,不问;② 这把钥单独记着(或本次运行信任过)→ 放行,不再看证书;
    /// ③ 对上这台主机的 CA 签发的证书 → 库验类型、签名、有效期、主体与关键选项,合格放行(不记进信任库:证书每次重签都会变,
    /// 下次照样靠 CA 验),不合格拒绝;④ 这台主机由 CA 管、出示的钥却没有那个 CA 担保 → 拒绝,<b>不退回首次确认</b> ——
    /// 给主机配 CA 要的正是「不再靠第一次盲信」,悄悄改走 TOFU 会把配置出错或路上有人这件事藏起来;
    /// ⑤ 都不沾才是原来那三条出路。
    /// </para>
    /// <para>
    /// 受信 CA 与吊销记录读不出来时照实抛出(连接失败):当成「没有」会把吊销了的钥、或只认证书的主机放回首次确认。
    /// </para>
    /// </remarks>
    public async ValueTask<SshHostKeyVerdict> EvaluateAsync(
        SshHostKeyContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        SecurityOptions security = GetSecurityOptions(settings);

        string host = context.Host;
        int port = context.Port;
        // 主机证书按证书里那把钥记、按那把钥比:证书每次重签 blob 都会变,钥不变
        // (指纹本身就是那把钥的,见 SshPublicKey.Sha256Fingerprint)。
        string fingerprint = context.Key.Sha256Fingerprint;
        string keyType = context.Key.PlainKeyType;
        string target = context.Target;

        IReadOnlyList<KnownHostEntry> markers = await LoadMarkerEntriesAsync(cancellationToken).ConfigureAwait(false);
        KnownHostLookup authority = markers.Count == 0
            ? default
            : KnownHostsFile.Lookup(markers, host, port, context.Key, Now);

        if (authority.Status == KnownHostStatus.Revoked)
        {
            string revoked = Strings.Format("Msg_HostKeyRevoked", target, fingerprint);
            await RaiseAsync("hostkey-rejected", revoked).ConfigureAwait(false);
            return SshHostKeyVerdict.Reject(revoked);
        }

        HostKeyVerification verification = await hostKey
            .VerifyHostKeyAsync(host, port, keyType, fingerprint, cancellationToken).ConfigureAwait(false);

        if (verification == HostKeyVerification.Trusted
            || HostTrustOnceCache.IsTrusted(host, port, fingerprint))
        {
            if (verification == HostKeyVerification.Trusted)
            {
                await BackfillPublicKeyAsync(context, cancellationToken).ConfigureAwait(false);
            }
            return SshHostKeyVerdict.Accept;
        }

        switch (authority.Status)
        {
            case KnownHostStatus.Known:
                return SshHostKeyVerdict.Accept;

            case KnownHostStatus.CertificateInvalid:
            {
                System.Diagnostics.Trace.WriteLine($"[VelaShell] host certificate rejected for {target}: {authority.CertificateProblem}");
                string invalid = Strings.Format("Msg_HostCertInvalid", target, DescribeCertificateProblem(context.Key, host, Now),
                    FingerprintOf(context.Key.Certificate?.SignatureKey));
                await RaiseAsync("hostkey-rejected", invalid).ConfigureAwait(false);
                return SshHostKeyVerdict.Reject(invalid);
            }

            case KnownHostStatus.OtherKeyTypesKnown:
            {
                // 这台主机由 CA 管:出示的是普通钥,或者别的 CA 签的证书,而这把钥又没有单独记着。
                string patterns = authority.ConflictingEntries
                    .Where(entry => entry.IsCertificateAuthority)
                    .Select(entry => string.Join(',', entry.Patterns))
                    .FirstOrDefault() ?? "*";
                string unvouched = Strings.Format("Msg_HostCaUnvouched", target, patterns, fingerprint);
                await RaiseAsync("hostkey-rejected", unvouched).ConfigureAwait(false);
                return SshHostKeyVerdict.RejectChanged(unvouched);
            }
        }

        // 变更时把旧指纹取出来,弹窗与错误文案都要摆它 —— 用户靠两把对照才判断得了
        // 这是自己刚重装的那台,还是路上有人。读不到就当没有,绝不因此阻断判定。
        string? knownFingerprint = null;
        if (verification == HostKeyVerification.Changed)
        {
            try
            {
                // 摆同一种类型的那一把(真换了钥);没有这种类型的,摆最近见过的那一把(服务端换了类型)。
                IReadOnlyList<KnownHost> known = await hostKey.FindKnownHostKeysAsync(host, port, cancellationToken)
                    .ConfigureAwait(false);
                knownFingerprint = (known.FirstOrDefault(entry => string.Equals(entry.KeyType, keyType, StringComparison.Ordinal))
                                    ?? (known.Count > 0 ? known[0] : null))?.Fingerprint;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Trace.WriteLine($"[VelaShell] known_hosts lookup failed: {ex}");
            }
        }

        bool firstSeen = verification == HostKeyVerification.Unknown;
        bool askUser = prompt is not null
                       && (firstSeen ? security.ConfirmFirstFingerprint : !security.BlockOnFingerprintChange);

        // 自动重连没有人在等:本该弹窗问的,直接拒绝(见 ConnectionInfo.IsAutomaticReconnect)。
        // 不问的那两条默认不受影响 —— 用户关掉了「首次连接确认」时照旧按 TOFU 记下,「变更即阻断」照旧阻断。
        // 照样记一笔安全告警:指纹变了却没有人看到弹窗,审计里至少要留下这件事。
        if (askUser && automaticReconnect)
        {
            string blocked = Strings.Format("Msg_HostKeyAutoReconnectBlocked", target, fingerprint);
            if (alerts is not null)
            {
                await alerts.RaiseAsync("hostkey-rejected", blocked).ConfigureAwait(false);
            }
            return verification == HostKeyVerification.Changed
                ? SshHostKeyVerdict.RejectChanged(blocked)
                : SshHostKeyVerdict.Reject(blocked);
        }

        HostKeyDecision decision = askUser
            // 出示的是主机证书、却没有受信 CA 为它担保时,把签发 CA 的指纹也摆出来:
            // 用户核对之后把那个 CA 加进信任库,同一个 CA 签的主机就不必逐台确认。
            ? await prompt!.DecideAsync(host, port, keyType, fingerprint, verification, knownFingerprint, context.Key.RandomArt,
                    FingerprintOf(context.Key.Certificate?.SignatureKey))
                .ConfigureAwait(false)
            // 不问的两条默认:首次连接按 TOFU 记下,指纹变更 fail-closed 拒掉。
            : firstSeen
                ? HostKeyDecision.TrustPermanently
                : HostKeyDecision.Reject;

        switch (decision)
        {
            case HostKeyDecision.Reject:
                alerts?.RaiseAsync("hostkey-rejected",
                    Strings.Format("KeySvc_AlertFirstRejected", target, fingerprint));
                string rejection = DescribeRejection(target, fingerprint, knownFingerprint, verification, askUser);
                return verification == HostKeyVerification.Changed
                    ? SshHostKeyVerdict.RejectChanged(rejection)
                    : SshHostKeyVerdict.Reject(rejection);

            case HostKeyDecision.TrustOnce:
                HostTrustOnceCache.Remember(host, port, fingerprint);
                if (alerts is not null)
                {
                    await alerts.RaiseAsync("hostkey-trusted-once",
                        verification == HostKeyVerification.Changed
                            ? Strings.Format("KeySvc_AlertChangedTrustOnce", target, fingerprint)
                            : Strings.Format("KeySvc_AlertFirstTrustOnce", target, fingerprint))
                        .ConfigureAwait(false);
                }
                return SshHostKeyVerdict.Accept;

            default:
                if (verification == HostKeyVerification.Changed && alerts is not null)
                {
                    await alerts.RaiseAsync("hostkey-changed-accepted",
                        Strings.Format("KeySvc_AlertChangedAccepted", target, fingerprint)).ConfigureAwait(false);
                }
                // 真正落盘交给 PersistAsync —— 库只在握手确实走到这一步之后才调它。
                return SshHostKeyVerdict.AcceptAndPersist;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 裁决与落盘分成两步是库的设计:裁决那一步跑在独立的计时里(可能要等用户),
    /// 落盘则跟着连接本身的取消令牌走。两者混在一个回调里时,
    /// 写 known_hosts 这件事会被裁决的超时口径一起管住 —— 那是不对的。
    /// <para>
    /// 落盘发生在密钥交换阶段、认证**之前**,与 OpenSSH 的时机一致:
    /// 主机身份在 KEX 就已经证明完了,认证成不成功是另一回事。
    /// </para>
    /// <para>
    /// 记不下来(信任库出错)时库照常连下去,失败记在连接的 <c>HostKeyPersistFailure</c> 上
    /// (velashell-docs/zh/ssh/spec/03 §5.4)。这里补两件只有宿主做得了的事:本次运行里记住这把指纹,
    /// 下一次连接别再弹窗;再告诉用户一声 —— 不然重启之后又被问一遍,他不会知道为什么。
    /// </para>
    /// </remarks>
    public async ValueTask PersistAsync(
        SshHostKeyContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await hostKey.TrustHostKeyAsync(
                context.Host, context.Port, context.Key.PlainKeyType, context.Key.Sha256Fingerprint, PublicKeyOf(context.Key), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HostTrustOnceCache.Remember(context.Host, context.Port, context.Key.Sha256Fingerprint);
            if (alerts is not null)
            {
                await alerts.RaiseAsync("hostkey-persist-failed",
                    Strings.Format("KeySvc_AlertPersistFailed", context.Target, context.Key.Sha256Fingerprint, ex.Message))
                    .ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>被拒时给用户看的那句话:说清是哪台、哪把指纹、以及下一步去哪操作。</summary>
    private static string DescribeRejection(
        string target, string fingerprint, string? knownFingerprint,
        HostKeyVerification verification, bool askedUser) =>
        verification == HostKeyVerification.Changed && !askedUser
            // 阻断开关开着:用户根本没被问过,得告诉他这是策略拦的,以及两条出路。
            ? Strings.Format("Msg_HostKeyChangedBlocked", target, knownFingerprint ?? "-", fingerprint)
            : Strings.Format("Msg_HostKeyRejected", target, fingerprint);

    /// <summary>
    /// 证书为什么不合格,换成界面语言(库给的 <see cref="KnownHostLookup.CertificateProblem" /> 是写给开发者的诊断文本,只进日志)。
    /// </summary>
    /// <remarks>
    /// 结论由库下(<see cref="KnownHostStatus.CertificateInvalid" />),这里只按证书公开的字段挑一句说得出口的原因:
    /// 类型、有效期、主体、关键选项逐条看,都对得上才是签名那一条(签名本身只有库验得了)。每一句都是证书上的事实。
    /// </remarks>
    internal static string DescribeCertificateProblem(SshPublicKey key, string host, DateTimeOffset now)
    {
        if (key.Certificate is not { } certificate)
        {
            return Strings.Get("HostCert_ProblemSignature");
        }
        if (certificate.CertificateType != SshCertificateType.Host)
        {
            return Strings.Get("HostCert_ProblemNotHost");
        }
        if (!certificate.IsTimeValid(now))
        {
            return certificate.ValidBeforeTime is { } before && now >= before
                ? Strings.Format("HostCert_ProblemExpired", before.ToString("u", CultureInfo.InvariantCulture))
                : Strings.Format("HostCert_ProblemNotYetValid",
                    (certificate.ValidAfterTime ?? DateTimeOffset.MaxValue).ToString("u", CultureInfo.InvariantCulture));
        }
        if (certificate.ValidPrincipals.Count == 0)
        {
            return Strings.Get("HostCert_ProblemNoPrincipals");
        }
        if (!certificate.ValidPrincipals.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return Strings.Format("HostCert_ProblemPrincipals", host, string.Join(", ", certificate.ValidPrincipals));
        }
        return certificate.CriticalOptions.Count > 0
            ? Strings.Format("HostCert_ProblemCriticalOptions", string.Join(", ", certificate.CriticalOptions))
            : Strings.Get("HostCert_ProblemSignature");
    }

    /// <summary>
    /// 记着的这把钥还没有公钥原文(换库之前、或这个字段加进来之前信任的,只存了指纹)时顺手补上 ——
    /// 不补的话这条记录导不出 OpenSSH 的 <c>known_hosts</c>。补不上不影响这次连接。
    /// </summary>
    private async Task BackfillPublicKeyAsync(SshHostKeyContext context, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<KnownHost>? known = await hostKey.FindKnownHostKeysAsync(context.Host, context.Port, cancellationToken)
                .ConfigureAwait(false);
            KnownHost? entry = known?.FirstOrDefault(k => SonnetDbHostKeyService.SameFingerprint(k.Fingerprint, context.Key.Sha256Fingerprint));
            if (entry is not null && string.IsNullOrEmpty(entry.HostKey))
            {
                await hostKey.TrustHostKeyAsync(context.Host, context.Port, context.Key.PlainKeyType, context.Key.Sha256Fingerprint,
                    PublicKeyOf(context.Key), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.WriteLine($"[VelaShell] known_hosts public key backfill failed: {ex}");
        }
    }

    /// <summary>记进信任库的公钥原文:证书里那把普通钥的 blob(base64),与 <c>known_hosts</c> 第三栏同一个东西。</summary>
    private static string PublicKeyOf(SshPublicKey key) => Convert.ToBase64String(key.PlainKey.Blob.Span);

    /// <summary>此刻(验证书有效期用)。</summary>
    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();

    /// <summary>一把钥的 SHA-256 指纹;没有钥时为 <see langword="null" />。</summary>
    private static string? FingerprintOf(SshPublicKey? key) => key?.Sha256Fingerprint;

    private Task RaiseAsync(string category, string message) =>
        alerts is null ? Task.CompletedTask : alerts.RaiseAsync(category, message);

    private static SecurityOptions GetSecurityOptions(ISettingsService? settings)
    {
        try
        {
            return settings.GetSnapshotBlocking().Security;
        }
        catch
        {
            return new();
        }
    }
}
