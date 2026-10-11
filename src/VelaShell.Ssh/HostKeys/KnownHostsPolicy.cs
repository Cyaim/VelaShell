// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3、§5.4、§5.5

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.HostKeys;

/// <summary>按 <c>known_hosts</c> 裁决主机密钥。</summary>
/// <remarks>
/// <para>
/// <b>「密钥变了」与「没见过」是两件完全不同的事，所以走两条不同的路。</b>
/// </para>
/// <para>
/// 没见过 → 可以问、可以记（TOFU）。
/// 变了 → <b>默认直接拒绝，并且不提供「记住新的」这条捷径</b>：
/// 它可能是中间人，也可能只是服务器重装了，而这两者在协议层无法区分。
/// 要接受新密钥，使用者得先把旧的那一行删掉 —— 那一下手动操作正是让人
/// 停下来想一想的地方。异常消息里会指出是文件的第几行。
/// </para>
/// </remarks>
/// <remarks>用给定的 <c>known_hosts</c> 路径构造。</remarks>
/// <param name="path"><c>known_hosts</c> 路径；<see langword="null"/> 表示用默认路径。</param>
/// <param name="askUnknownHost">
/// 没见过这台主机时问使用者。返回 <see langword="true"/> 表示信任并记下来。
/// </param>
public sealed class KnownHostsPolicy(
    string? path = null,
    Func<SshHostKeyContext, CancellationToken, ValueTask<bool>>? askUnknownHost = null) : IHostKeyPolicy, IHostKeyTypePreference, IHostKeyRotationPolicy
{
    private readonly string _path = path ?? KnownHostsFile.DefaultPath;
    private readonly Func<SshHostKeyContext, CancellationToken, ValueTask<bool>>? _askUnknownHost = askUnknownHost;
    private IReadOnlyList<KnownHostEntry>? _cache;

    /// <summary>不读也不写任何文件：每台主机都当成没见过，接受了也不记（<c>UserKnownHostsFile none</c> / <c>/dev/null</c>）。</summary>
    /// <param name="askUnknownHost">没见过这台主机时问使用者。只在 <paramref name="unknownHost"/> 是 <see cref="UnknownHostBehavior.Ask"/> 时能给。</param>
    /// <param name="unknownHost">没见过这台主机时的行为（见 <see cref="UnknownHost"/>）。</param>
    public static KnownHostsPolicy WithoutFile(
        Func<SshHostKeyContext, CancellationToken, ValueTask<bool>>? askUnknownHost = null,
        UnknownHostBehavior unknownHost = UnknownHostBehavior.Ask) =>
        new(NoFileMarker, askUnknownHost) { UnknownHost = unknownHost, _withoutFile = true, _cache = [] };

    /// <summary>不用文件时 <see cref="_path"/> 的取值，只出现在错误消息里。</summary>
    private const string NoFileMarker = "（不使用 known_hosts）";

    private bool _withoutFile;

    /// <summary>没见过这台主机时的行为。</summary>
    /// <exception cref="ArgumentOutOfRangeException">不是定义过的值。</exception>
    /// <exception cref="ArgumentException">
    /// 给了询问回调（构造函数的 <c>askUnknownHost</c>），又设成不问 —— 回调永远不会被调用。曾经静默忽略回调。
    /// </exception>
    public UnknownHostBehavior UnknownHost
    {
        get;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(UnknownHost), value, "不认识的取值。");
            }
            if (value != UnknownHostBehavior.Ask && _askUnknownHost is not null)
            {
                throw new ArgumentException(
                    $"给了询问回调，又把没见过的主机设成 {value} —— 回调永远不会被调用。二者只取其一。", nameof(UnknownHost));
            }
            field = value;
        }
    } = UnknownHostBehavior.Ask;

    /// <summary>
    /// 允许在密钥变化时也接受。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>默认关闭，而且不该打开。</b>
    /// 这个开关存在只是为了「我知道我刚重装了那台机器」这一种情形，
    /// 而那种情形下更好的做法是去把 <c>known_hosts</c> 里那一行删掉。
    /// </remarks>
    public bool DangerousAcceptChangedKeys { get; init; }

    /// <summary>写入时是否把主机名散列掉（对应 <c>HashKnownHosts yes</c>）。</summary>
    public bool IsHashingHostNames { get; init; }

    /// <summary>
    /// 主机密钥轮换（<c>UpdateHostKeys yes</c>，见 <see cref="IHostKeyRotationPolicy"/>）：服务端证明持有的新主机密钥补记进 <c>known_hosts</c>，
    /// 不再出示的旧钥从里面删掉（只删专属于这台主机的记录，见 <see cref="KnownHostsFile.RemoveHostKeysAsync(string, int, IReadOnlyCollection{string}?, string?, CancellationToken)"/>）。
    /// 默认 <see langword="false"/>。不用文件（<see cref="WithoutFile"/>）时打开了也不记、不删。
    /// </summary>
    public bool AllowHostKeyUpdates { get; init; }

    /// <summary>
    /// 只读的全局 known_hosts（<c>ssh_config</c> 的 <c>GlobalKnownHostsFile</c>，如 <c>/etc/ssh/ssh_known_hosts</c>）：
    /// 查的时候与自己的那份一起看（<c>@revoked</c>、<c>@cert-authority</c> 照样算），记的时候只写自己的那份。
    /// </summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/09 §7〕默认空 —— 不去读系统目录里的那一份：给了才读。读不到的（不存在、没权限）照没有处理。
    /// </remarks>
    public IReadOnlyList<string> GlobalKnownHostsFiles
    {
        get;
        init => field = value is null ? [] : Array.AsReadOnly([.. value]);
    } = [];

    /// <summary>自己的那份，加上全局的几份（读不到的跳过）。</summary>
    private async ValueTask<IReadOnlyList<KnownHostEntry>> LoadEntriesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<KnownHostEntry> own = await KnownHostsFile.LoadAsync(_path, cancellationToken).ConfigureAwait(false);
        if (GlobalKnownHostsFiles.Count == 0)
        {
            return own;
        }

        List<KnownHostEntry> all = [.. own];
        foreach (string path in GlobalKnownHostsFiles)
        {
            try
            {
                all.AddRange(await KnownHostsFile.LoadAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SshException)
            {
                // 全局的那几份读不到（不存在、没权限）照没有处理：它们只是额外的信任来源。
            }
        }
        return all;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> GetKnownHostKeyFingerprintsAsync(
        string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        IReadOnlyList<KnownHostEntry> entries =
            _cache ??= await LoadEntriesAsync(cancellationToken).ConfigureAwait(false);
        return [.. KnownHostsFile.KnownHostKeys(entries, host, port).Select(key => key.Sha256Fingerprint)];
    }

    /// <inheritdoc />
    public async ValueTask RecordHostKeysAsync(
        string host, int port, IReadOnlyList<SshPublicKey> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(keys);
        if (_withoutFile || !AllowHostKeyUpdates || !KnownHostsFile.IsRecordableHost(host))
        {
            return;
        }

        foreach (SshPublicKey key in keys)
        {
            await KnownHostsFile.AppendAsync(host, port, key, _path, IsHashingHostNames, cancellationToken).ConfigureAwait(false);
        }
        _cache = null;
    }

    /// <inheritdoc />
    /// <remarks>只删自己那份文件里专属于这台主机的记录；全局的几份只读，记在那里的旧钥删不了，不算进返回值。</remarks>
    public async ValueTask<IReadOnlyList<string>> ForgetHostKeysAsync(
        string host, int port, IReadOnlyList<string> fingerprints, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(fingerprints);
        if (_withoutFile || !AllowHostKeyUpdates || fingerprints.Count == 0)
        {
            return [];
        }

        IReadOnlyList<string> removed = await KnownHostsFile.RemoveHostKeysAsync(host, port, fingerprints, _path, cancellationToken)
            .ConfigureAwait(false);
        _cache = null;
        return removed;
    }

    /// <summary>
    /// 删掉这台主机在自己那份 <c>known_hosts</c> 里记着的普通主机密钥 —— 「密钥变了」、确认是服务器重装之后一键删掉旧的记录，下次连接按「没见过」处理。
    /// </summary>
    /// <param name="host">主机（与裁决时同一个名字）。</param>
    /// <param name="port">端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删掉的钥的指纹；不用文件时、没有可删的时为空。</returns>
    /// <exception cref="SshConnectException">改写不了：<see cref="SshFailureReason.HostKeyStoreFailed"/>。</exception>
    /// <remarks>
    /// 〔Q4，velashell-docs/zh/ssh/spec/03 §5.4〕只删专属于这台主机的记录（规则见
    /// <see cref="KnownHostsFile.RemoveHostKeysAsync(string, int, IReadOnlyCollection{string}?, string?, CancellationToken)"/>）：
    /// <c>@revoked</c>、<c>@cert-authority</c>、通配行与全局的几份都不动 —— 冲突要是出在那些行上，删完再连照样报「变了」，消息里有行号。
    /// 该不该删由调用方（使用者）决定：本库从不自己删，「变了」时的裁决照旧是拒绝。
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> RemoveHostKeysAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_withoutFile)
        {
            return [];
        }

        IReadOnlyList<string> removed = await KnownHostsFile.RemoveHostKeysAsync(host, port, fingerprints: null, _path, cancellationToken)
            .ConfigureAwait(false);
        _cache = null;
        return removed;
    }

    /// <inheritdoc />
    public async ValueTask<SshHostKeyVerdict> EvaluateAsync(
        SshHostKeyContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<KnownHostEntry> entries =
            _cache ??= await LoadEntriesAsync(cancellationToken).ConfigureAwait(false);

        KnownHostLookup lookup = KnownHostsFile.Lookup(entries, context.Host, context.Port, context.Key);

        return lookup.Status switch
        {
            KnownHostStatus.Known => SshHostKeyVerdict.Accept,
            KnownHostStatus.Revoked => SshHostKeyVerdict.Reject(
                                $"{context.Target} 出示的主机密钥在 {_path} 里被标记为 @revoked" +
                                $"（第 {lookup.MatchedEntry?.LineNumber} 行）。" +
                                "这把密钥已经作废 —— 不要连。"),
            KnownHostStatus.Changed => DangerousAcceptChangedKeys
                                ? SshHostKeyVerdict.Accept
                                : SshHostKeyVerdict.RejectChanged(BuildChangedMessage(context, lookup)),
            KnownHostStatus.CertificateInvalid => SshHostKeyVerdict.Reject(
                                $"{context.Target} 出示了主机证书，签发它的 CA 在 {_path} 里对上了这台主机" +
                                $"（第 {lookup.MatchedEntry?.LineNumber} 行），但证书不合格：{lookup.CertificateProblem}" + Environment.NewLine +
                                "这台主机由 CA 管理，证书不合格说明配置出了错，或者路上有人 —— 不会退回去按新主机询问。" +
                                "请联系管理员重新签发主机证书。"),
            // 与「变了」同样处理：连接时已经把记着的类型排在最前，正常的服务端会谈成它；
            // 还落到这里，要么服务端不再有那把钥，要么路上有人。**不能**当成「没见过」去问、去记。
            KnownHostStatus.OtherKeyTypesKnown => DangerousAcceptChangedKeys
                                ? SshHostKeyVerdict.Accept
                                : SshHostKeyVerdict.RejectChanged(BuildOtherTypeMessage(context, lookup)),
            _ => await HandleUnknownAsync(context, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> GetKnownKeyTypesAsync(
        string host, int port, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<KnownHostEntry> entries =
            _cache ??= await LoadEntriesAsync(cancellationToken).ConfigureAwait(false);
        return KnownHostsFile.KnownKeyTypes(entries, host, port);
    }

    private string BuildOtherTypeMessage(SshHostKeyContext context, KnownHostLookup lookup)
    {
        string known = string.Join(
            "、", lookup.ConflictingEntries.Select(e => e.IsCertificateAuthority
                ? $"@cert-authority {e.KeyType}（第 {e.LineNumber} 行）"
                : $"{e.KeyType}（第 {e.LineNumber} 行）"));

        bool managedByCa = lookup.ConflictingEntries.Any(static e => e.IsCertificateAuthority);
        return
            $"{context.Target} 这次出示的是 {context.Key.KeyType} 密钥 {context.Key.Sha256Fingerprint}，" + Environment.NewLine +
            $"而 {_path} 里记着的是别的类型：{known}。" + Environment.NewLine +
            (managedByCa
                ? "这台主机由 CA 管理，应当出示那个 CA 签发的主机证书；出示一把没有 CA 担保的钥，可能是路上有人 —— 所以不能把它当成新主机。" + Environment.NewLine +
                  $"如果确认这台主机确实没有证书，核对指纹之后把它的公钥单独加进 {_path}。"
                : "换一种没记过的密钥类型，是绕过「主机密钥变了」检查的一种办法 —— 所以不能把它当成新主机。" + Environment.NewLine +
                  $"如果确认服务端不再有原来那把钥，把 {_path} 里上面那几行删掉再连。");
    }

    private string BuildChangedMessage(SshHostKeyContext context, KnownHostLookup lookup)
    {
        string lines = string.Join(
            "、", lookup.ConflictingEntries.Select(e => $"第 {e.LineNumber} 行"));

        return
            $"{context.Target} 的主机密钥变了。" + Environment.NewLine +
            $"对端现在出示：{context.Key.Sha256Fingerprint}" +
            $"（{context.Key.KeyType}，{context.Key.KeyBits} 位）" + Environment.NewLine +
            $"而 {_path} 里记的是另一把（{lines}）。" + Environment.NewLine +
            "这可能是中间人攻击，也可能只是那台服务器重装了 —— " +
            "协议层分不出这两者，所以得由你来判断。" + Environment.NewLine +
            $"确认是重装之后，把 {_path} 里上面那一行删掉再连。";
    }

    private async ValueTask<SshHostKeyVerdict> HandleUnknownAsync(
        SshHostKeyContext context, CancellationToken cancellationToken)
    {
        switch (UnknownHost)
        {
            case UnknownHostBehavior.AcceptAndPersist:
                return SshHostKeyVerdict.AcceptAndPersist;

            case UnknownHostBehavior.Reject:
                return SshHostKeyVerdict.Reject(
                    $"没见过 {context.Target} 这台主机，而策略是不接受新主机。" +
                    $"对端出示：{context.Key.Sha256Fingerprint}（{context.Key.KeyType}）。" +
                    $"确认无误后可以手动加进 {_path}。");

            default:
                if (_askUnknownHost is null)
                {
                    return SshHostKeyVerdict.Reject(
                        $"没见过 {context.Target} 这台主机，而这个策略没有配询问回调。" +
                        $"对端出示：{context.Key.Sha256Fingerprint}（{context.Key.KeyType}）。");
                }

                bool trusted = await _askUnknownHost(context, cancellationToken).ConfigureAwait(false);
                return trusted
                    ? SshHostKeyVerdict.AcceptAndPersist
                    : SshHostKeyVerdict.Reject($"使用者拒绝信任 {context.Target} 的主机密钥。");
        }
    }

    /// <inheritdoc />
    public async ValueTask PersistAsync(SshHostKeyContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_withoutFile)
        {
            return;   // 配置说了不用 known_hosts：记到哪里去都不对
        }

        // 〔velashell-docs/zh/ssh/spec/03 §5.4〕主机名里有 known_hosts 另有含义的字符时不写、这次连接也不放行：
        // 使用者说的是「信任并记住」，记不下来就不该悄悄当成「只信这一次」；而这样的名字本来就不是一台主机。
        if (!KnownHostsFile.IsRecordableHost(context.Host))
        {
            throw new SshConnectException(
                SshFailureReason.InvalidConfiguration, SshPhase.KeyExchange,
                $"主机名 {PeerText.Sanitize(context.Host)} 里有 known_hosts 里另有含义的字符" +
                "（, * ? ! [ ] # 空白、控制字符，或开头的 @ |），不能记进 known_hosts。");
        }

        await KnownHostsFile.AppendAsync(
            context.Host, context.Port, context.Key, _path, IsHashingHostNames, cancellationToken)
            .ConfigureAwait(false);

        // 缓存作废 —— 下一次裁决要看到刚写进去的这一条。
        _cache = null;
    }
}
