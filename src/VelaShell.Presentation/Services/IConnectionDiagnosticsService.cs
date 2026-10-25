using VelaShell.Core.Models;
using VelaShell.Core.Ssh;

namespace VelaShell.Presentation.Services;

/// <summary>诊断步骤状态(设计 RGXg1:✅ 成功 / ⚠ 警告 / ✗ 失败 / ⏸ 跳过)。</summary>
public enum DiagnosticStepStatus
{
    /// <summary>等待执行,尚未开始。</summary>
    Pending,

    /// <summary>正在执行中。</summary>
    Running,

    /// <summary>执行成功。</summary>
    Success,

    /// <summary>执行完成但存在警告。</summary>
    Warning,

    /// <summary>执行失败。</summary>
    Failed,

    /// <summary>因前置条件不满足等原因被跳过。</summary>
    Skipped
}

/// <summary>单个诊断步骤的即时快照;Index 对应固定的四步顺序。</summary>
/// <param name="Index">步骤序号,对应固定的四步诊断顺序。</param>
/// <param name="Name">步骤名称。</param>
/// <param name="Status">步骤当前状态。</param>
/// <param name="Detail">步骤的补充说明或错误详情,可为 <see langword="null" />。</param>
/// <param name="ElapsedMs">步骤耗时(毫秒),未完成时可为 <see langword="null" />。</param>
public sealed record DiagnosticStepUpdate(
    int Index,
    string Name,
    DiagnosticStepStatus Status,
    string? Detail = null,
    long? ElapsedMs = null);

/// <summary>一次完整诊断的结果:四个步骤 + 发现的问题与修复建议(设计 RGXg1)。</summary>
public sealed class DiagnosticReport
{
    /// <summary>本次诊断的四个步骤及其最终状态。</summary>
    public required IReadOnlyList<DiagnosticStepUpdate> Steps { get; init; }

    /// <summary>发现的问题标题;无问题时为 <see langword="null" />。</summary>
    public string? IssueTitle { get; init; }

    /// <summary>发现问题的详细描述;无问题时为 <see langword="null" />。</summary>
    public string? IssueDescription { get; init; }

    /// <summary>针对所发现问题给出的修复建议列表。</summary>
    public IReadOnlyList<string> Suggestions { get; init; } = [];

    /// <summary>
    /// 认证那一步(完整连接,含跳板链)的协议轨迹:每个报文的方向、类型、长度、序号与通道号,<b>不含内容</b>。
    /// 那一步没有走到 SSH 连接(前面的步骤没过、没有凭据、插件协议)时为空。
    /// </summary>
    public IReadOnlyList<SshPacketTraceEntry> PacketTrace { get; init; } = [];

    /// <summary>超过记录上限、没记进 <see cref="PacketTrace" /> 的报文数。</summary>
    public int PacketTraceDropped { get; init; }

    /// <summary>
    /// 认证那一步(完整连接)失败时的原始异常;没走到这一步、或者走通了时为 <see langword="null" />。
    /// </summary>
    /// <remarks>
    /// 协商在这一步才发生:宿主据它认出「算法谈不成」并给出一键放开(<c>SshAlgorithmRemedy</c>),
    /// 会话健康巡检据它分出「主机密钥待确认」这一类要人来处理的失败。
    /// </remarks>
    public Exception? Failure { get; init; }

    /// <summary>诊断是否整体成功(即未发现任何问题)。</summary>
    public bool Success => IssueTitle is null;
}

/// <summary>连接诊断中心(设计 RGXg1):逐步分析 DNS、TCP、SSH 握手与用户认证。</summary>
public interface IConnectionDiagnosticsService
{
    /// <summary>对一条连接配置执行逐步诊断;<paramref name="progress" /> 实时上报每步状态。</summary>
    Task<DiagnosticReport> DiagnoseAsync(
        SessionProfile profile,
        IProgress<DiagnosticStepUpdate>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 没人看着的诊断(会话健康巡检):同样四步,认证那一步不弹任何框
    /// (<see cref="IConnectionWorkflowService.TestConnectionUnattendedAsync" />);没保存凭据的照旧跳过、记成警告。
    /// </summary>
    /// <param name="profile">要诊断的配置。</param>
    /// <param name="verifyLogin">是否走到认证那一步;为 false 时只查到 SSH 握手,不往服务器上登录。</param>
    /// <param name="progress">各步骤状态变更的进度回调,可为 null。</param>
    /// <param name="cancellationToken">取消令牌(停下巡检、单台到点);取消照原样抛出。</param>
    Task<DiagnosticReport> DiagnoseUnattendedAsync(
        SessionProfile profile,
        bool verifyLogin,
        IProgress<DiagnosticStepUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}
