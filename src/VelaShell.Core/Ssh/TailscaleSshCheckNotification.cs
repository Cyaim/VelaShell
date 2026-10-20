namespace VelaShell.Core.Ssh;

/// <summary>一次实际 SSH 认证跳的验证窗口生命周期；空链接表示结束。</summary>
/// <param name="AttemptId">一次实际认证跳的标识，不是会话配置标识。</param>
/// <param name="Target">当前跳的 user@host:port。</param>
/// <param name="VerificationUri">当前可操作的验证链接；为空表示结束。</param>
/// <param name="Cancel">只取消本次握手的动作；结束通知为空。</param>
/// <param name="CancellationToken">验证窗口生命周期；结束通知不可取消。</param>
public sealed record TailscaleSshCheckNotification(
    Guid AttemptId,
    string Target,
    Uri? VerificationUri,
    Action? Cancel,
    CancellationToken CancellationToken)
{
    /// <summary>本次等待验证的截止时刻；到点仍未批准则本次认证超时。结束通知为空。</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>仅格式化类型和尝试标识，绝不暴露验证链接。</summary>
    public override string ToString() => $"{nameof(TailscaleSshCheckNotification)} {{ AttemptId = {AttemptId} }}";
}
