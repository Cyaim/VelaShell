// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/04-authentication.md §4;velashell-docs/zh/ssh/spec/08-failures.md

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Keys;

/// <summary>私钥读不出来：文件、格式、口令、算法。</summary>
/// <remarks>
/// 具体是哪一种看 <see cref="SshException.Reason"/>：<see cref="SshFailureReason.KeyFileUnreadable"/>、
/// <see cref="SshFailureReason.KeyFormatInvalid"/>、<see cref="SshFailureReason.KeyPassphraseRequired"/>、
/// <see cref="SshFailureReason.KeyPassphraseIncorrect"/>、<see cref="SshFailureReason.Unsupported"/>。
/// 读私钥发生在连接之外，所以阶段是 <see cref="SshPhase.None"/>。
/// </remarks>
/// <remarks>创建一个私钥读取异常。</remarks>
/// <param name="reason">失败的分类。</param>
/// <param name="message">诊断消息。</param>
/// <param name="innerException">内部异常。</param>
public sealed class SshPrivateKeyException(SshFailureReason reason, string message, Exception? innerException = null) : SshException(reason, SshPhase.None, message, innerException)
{

    /// <summary>是不是口令的问题（没给，或者给错了）—— 调用方据此再问一次口令。</summary>
    public bool NeedsPassphrase =>
        Reason is SshFailureReason.KeyPassphraseRequired or SshFailureReason.KeyPassphraseIncorrect;

    /// <summary>具体是哪种情形 —— 界面按它说话，不解析 <see cref="Exception.Message"/>。</summary>
    /// <remarks>库自己抛的都填；使用者自己构造、没填时是 <see cref="SshKeyProblemKind.Unknown"/>。</remarks>
    public SshKeyProblemKind ProblemKind { get; init; }

    /// <summary>
    /// 出事的私钥从哪来：<c>LoadAsync</c> 的文件路径、<c>Parse</c> 的 <c>origin</c>、系统密钥库里的钥名；不知道时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 由读私钥的入口在异常离开库之前补上（解析的内层不知道来源，只知道拼进消息的那段「（来源）」），所以是 <c>internal set</c>。
    /// </remarks>
    public string? Origin { get; internal set; }

    /// <summary>
    /// 不支持的那个名字（<see cref="SshKeyProblemKind.UnsupportedKeyType"/> 的密钥类型、<see cref="SshKeyProblemKind.UnsupportedCurve"/> 的曲线、
    /// <see cref="SshKeyProblemKind.UnsupportedCipher"/> 的加密算法、<see cref="SshKeyProblemKind.UnsupportedKdf"/> 的口令派生方式、
    /// <see cref="SshKeyProblemKind.UnsupportedPointFormat"/> 的密钥类型）。来自文件内容，<b>未清洗</b>。
    /// </summary>
    public string? AlgorithmName { get; init; }
}
