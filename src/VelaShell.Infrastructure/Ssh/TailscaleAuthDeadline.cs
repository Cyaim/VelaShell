using VelaShell.Ssh.HostKeys;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// Tailscale 认证跳的两段期限:主机密钥裁决之后先给一段守卫期,收到合法验证链接再放宽到服务端的等待上限。
/// </summary>
/// <remarks>
/// <para>
/// 库的认证计时在认证开始时一次定死。check 模式下服务端把 none 请求挂起、发出验证链接,
/// 一直等到用户在浏览器里批准(最长约三十分钟)—— 两分钟一到就判超时,走 SSO + 多因素的用户根本来不及。
/// 所以这一跳把库的认证计时关掉,改由这里管本次尝试的取消源:没收到链接之前仍是守卫期
/// (服务端一声不吭时照旧尽快失败),收到第一条链接时放宽一次。
/// </para>
/// <para>
/// 守卫期从主机密钥裁决结束时起算:库没有「认证开始」的回调,裁决是认证之前最后一个看得见的点。
/// 包一层主机密钥策略、而不是建连一开始就起表,是为了不把用户看指纹的时间算进去 —— 与库的口径一致。
/// 类型偏好与主机密钥轮换原样转给里面那个策略。
/// </para>
/// </remarks>
/// <param name="inner">真正做裁决的主机密钥策略。</param>
/// <param name="attempt">本次尝试的取消源;到点即取消它。</param>
/// <param name="guard">收到验证链接之前的守卫期。</param>
/// <param name="checkWait">收到验证链接之后的等待上限。</param>
internal sealed class TailscaleAuthDeadline(
    IHostKeyPolicy inner, CancellationTokenSource attempt, TimeSpan guard, TimeSpan checkWait)
    : IHostKeyPolicy, IHostKeyTypePreference, IHostKeyRotationPolicy
{
    private readonly Lock _gate = new();
    private bool _armed;
    private bool _finished;
    private DateTimeOffset? _expiresAt;

    /// <summary>计时器是否已经起表:之后本次尝试被取消而用户与调用方都没取消,就是到点了。</summary>
    public bool IsArmed
    {
        get
        {
            lock (_gate)
            {
                return _armed;
            }
        }
    }

    /// <summary>收到验证链接:放宽到等待上限(只放宽一次,之后的新链接沿用同一个截止时刻)。</summary>
    /// <returns>截止时刻;尝试已经结束时为 <see langword="null" />。</returns>
    public DateTimeOffset? ExtendForCheck()
    {
        lock (_gate)
        {
            if (_finished)
            {
                return null;
            }
            if (_expiresAt is null)
            {
                _armed = true;
                _expiresAt = DateTimeOffset.Now + checkWait;
                attempt.CancelAfter(checkWait);
            }
            return _expiresAt;
        }
    }

    /// <summary>本次尝试结束(成功或失败):之后的裁决(连上之后的重协商)不再碰已释放的取消源。</summary>
    public void Finish()
    {
        lock (_gate)
        {
            _finished = true;
        }
    }

    /// <inheritdoc />
    public async ValueTask<SshHostKeyVerdict> EvaluateAsync(SshHostKeyContext context, CancellationToken cancellationToken = default)
    {
        SshHostKeyVerdict verdict = await inner.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            // 认证期间对端发起的重协商还会再裁决一次:守卫期只起一次表,不因此重置。
            if (!_finished && !_armed)
            {
                _armed = true;
                attempt.CancelAfter(guard);
            }
        }
        return verdict;
    }

    /// <inheritdoc />
    public ValueTask PersistAsync(SshHostKeyContext context, CancellationToken cancellationToken = default) =>
        inner.PersistAsync(context, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> GetKnownKeyTypesAsync(string host, int port, CancellationToken cancellationToken = default) =>
        inner is IHostKeyTypePreference preference
            ? preference.GetKnownKeyTypesAsync(host, port, cancellationToken)
            : ValueTask.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public bool AllowHostKeyUpdates => inner is IHostKeyRotationPolicy { AllowHostKeyUpdates: true };

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> GetKnownHostKeyFingerprintsAsync(string host, int port, CancellationToken cancellationToken = default) =>
        inner is IHostKeyRotationPolicy rotation
            ? rotation.GetKnownHostKeyFingerprintsAsync(host, port, cancellationToken)
            : ValueTask.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public ValueTask RecordHostKeysAsync(string host, int port, IReadOnlyList<SshPublicKey> keys, CancellationToken cancellationToken = default) =>
        inner is IHostKeyRotationPolicy rotation
            ? rotation.RecordHostKeysAsync(host, port, keys, cancellationToken)
            : ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ForgetHostKeysAsync(
        string host, int port, IReadOnlyList<string> fingerprints, CancellationToken cancellationToken = default) =>
        inner is IHostKeyRotationPolicy rotation
            ? rotation.ForgetHostKeysAsync(host, port, fingerprints, cancellationToken)
            : ValueTask.FromResult<IReadOnlyList<string>>([]);
}
