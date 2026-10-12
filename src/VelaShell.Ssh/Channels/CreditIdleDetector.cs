// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.2  CHANNEL_WINDOW_ADJUST 的语义
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §3.3（扩窗认「空等」）

namespace VelaShell.Ssh.Channels;

/// <summary>
/// 自适应窗口的「空等」判据：读的一方读空之后等下一包等得最长的一段，占最近两轮（上上次回补到这一次）的 1/4 以上，
/// 就算窗口吃紧（〔velashell-docs/zh/ssh/spec/05 §3.3〕）。
/// </summary>
/// <remarks>
/// <para>
/// 时刻由调用方给（<see cref="System.Diagnostics.Stopwatch"/> 刻度），判据本身不碰时钟：用例拿给定的到达序列逐拍核对。
/// 曾经只有经链路模拟的用例 —— 模拟器按「计划时刻」推下一次允许发送的时间，计时器醒晚了就把积压的一口气补发出去，
/// 在计时器松弛大的机器（macOS 的 CI）上「匀速到达」成了「成批到、批间空着」，那条用例测到的不是它想测的东西。
/// </para>
/// <para>
/// <see cref="NoteArrival"/> 只在接收循环上调，<see cref="EndRound"/> 与 <see cref="Discard"/> 只在回补泵上调；
/// 两边共用的只有这一轮的最长等待，经 <see cref="Interlocked"/> 交接。
/// </para>
/// </remarks>
internal sealed class CreditIdleDetector
{
    // 接收循环
    private long _lastArrivalAt;

    // 两边共用：接收循环往大里写，回补泵每轮取走清零
    private long _roundLongestWait;

    // 回补泵
    private long _previousRoundLongestWait;
    private long _lastAdjustAt, _adjustBeforeLastAt;

    /// <summary>来了一包数据。</summary>
    /// <param name="now">到达的时刻。</param>
    /// <param name="readerDrained">此前交给读的一方的都已读走（读空了在等）。通道的第一包由调用方传 <see langword="false"/>。</param>
    public void NoteArrival(long now, bool readerDrained)
    {
        if (readerDrained && _lastArrivalAt != 0)
        {
            long waited = now - _lastArrivalAt;
            long longest = Volatile.Read(ref _roundLongestWait);
            while (waited > longest)
            {
                long seen = Interlocked.CompareExchange(ref _roundLongestWait, waited, longest);
                if (seen == longest)
                {
                    break;
                }
                longest = seen;   // 回补泵刚取走清零，按新值再比
            }
        }
        _lastArrivalAt = now;
    }

    /// <summary>
    /// 一轮结束（回补时调）：这一轮算不算空等。每轮都要调 —— 它顺带把这一轮的记录挪成「上一轮」。
    /// </summary>
    /// <param name="now">回补的时刻。</param>
    /// <param name="eligible">窗口够 4 个报文；不够时恒为 <see langword="false"/>（只认见底）。</param>
    public bool EndRound(long now, bool eligible)
    {
        long thisRound = Interlocked.Exchange(ref _roundLongestWait, 0);
        long longest = Math.Max(thisRound, _previousRoundLongestWait);
        long span = _adjustBeforeLastAt == 0 ? 0 : now - _adjustBeforeLastAt;
        _previousRoundLongestWait = thisRound;
        _adjustBeforeLastAt = _lastAdjustAt;
        _lastAdjustAt = now;
        return eligible && span > 0 && longest * 4 >= span;
    }

    /// <summary>扩过一次窗口：上一轮记下的空档作废 —— 再扩要有新一轮的空等作证，一段空档不连扩两回。</summary>
    public void Discard() => _previousRoundLongestWait = 0;
}
