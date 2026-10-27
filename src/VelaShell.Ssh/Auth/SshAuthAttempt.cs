// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/04-authentication.md §3.4;velashell-docs/zh/ssh/spec/08-failures.md §5.3

namespace VelaShell.Ssh.Auth;

/// <summary>认证过程中的一条尝试记录。</summary>
/// <param name="Method">认证方法名。</param>
/// <param name="CredentialLabel">凭据的标签。<b>不含任何密钥材料。</b></param>
/// <param name="Outcome">结果。</param>
/// <param name="ServerOfferedAfter">这一步之后服务端给出的可继续方法列表。</param>
/// <param name="Detail">
/// 补充说明，例如跳过或失败的具体原因。是拼好的中文句子（写给开发者与日志）；同一件事的结构化出处在 <see cref="Details"/>。
/// </param>
/// <param name="SignatureAlgorithm">
/// <c>publickey</c> 这一步用的签名算法（降级重试过的是重试那一个），如 <c>rsa-sha2-512</c>；别的方法、还没签就跳过的为 <see langword="null"/>。
/// </param>
/// <remarks>
/// 这张表会装进 <c>SshAuthenticationException</c>。它存在的理由很具体：
/// <b>要让「这台机器需要动态码」和「密码打错了」在 UI 上能区分开</b>，
/// 也要让「私钥文件读不出来」不再显示成「用户名或密码不正确」。
/// </remarks>
public readonly record struct SshAuthAttempt(
    string Method,
    string CredentialLabel,
    SshAuthOutcome Outcome,
    IReadOnlyList<string> ServerOfferedAfter,
    string? Detail = null,
    string? SignatureAlgorithm = null)
{
    /// <summary><see cref="Detail"/> 的结构化出处：这一步的几条说明，按出现顺序。没有说明时为空。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/04 §3.4〕界面要用别的语言显示尝试记录时按这里的 <see cref="SshAuthDetail.Kind"/> 与参数拼，
    /// 不去解析 <see cref="Detail"/> 的句子。<see cref="Detail"/> 由这几条的 <see cref="SshAuthDetail.ToString"/> 拼成。
    /// </remarks>
    public IReadOnlyList<SshAuthDetail> Details
    {
        get => field ?? [];
        init;
    }

    /// <summary>
    /// 这一步是口令凭据改走 <c>keyboard-interactive</c>（服务端不接受 <c>password</c>，velashell-docs/zh/ssh/spec/04 §6.5）时，
    /// 那条口令凭据的标签；别的尝试为 <see langword="null"/>。
    /// </summary>
    /// <remarks><see cref="CredentialLabel"/> 此时是这个标签后面接上「（经 keyboard-interactive）」的中文原文。</remarks>
    public string? BridgedCredentialLabel { get; init; }

    /// <summary>一行人话，用于日志与诊断面板。</summary>
    public override string ToString()
    {
        string outcome = Outcome switch
        {
            SshAuthOutcome.Success => "成功",
            SshAuthOutcome.PartialSuccess => "部分成功（服务端要求继续）",
            SshAuthOutcome.Failure => "失败",
            SshAuthOutcome.SkippedNotOffered => "跳过（服务端不接受这种方法）",
            SshAuthOutcome.SkippedNoMaterial => "跳过（凭据取不到材料）",
            _ => Outcome.ToString(),
        };
        string label = SignatureAlgorithm is null ? CredentialLabel : $"{CredentialLabel}（{SignatureAlgorithm}）";
        return string.IsNullOrEmpty(Detail)
            ? $"{label}：{outcome}"
            : $"{label}：{outcome} —— {Detail}";
    }
}
