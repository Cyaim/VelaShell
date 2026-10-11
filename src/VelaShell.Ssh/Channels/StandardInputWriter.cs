// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §4.3

using System.IO.Pipelines;

namespace VelaShell.Ssh.Channels;

/// <summary>交给调用方的 stdin writer：通道不再收数据之后，写与冲刷当场返回 <see cref="FlushResult.IsCompleted"/>。</summary>
/// <remarks>
/// <para>
/// 底下那条 <see cref="Pipe"/> 的 writer 要等 reader 完成才报「完成」，而 reader 归 stdin 泵：
/// 泵在通道收尾之后才在线程池上退出、完成它（不能从外面替它完成，见 <c>SshChannel.FinishClose</c>）。
/// 曾经直接把那个 writer 交出去 —— 调用方已经看到通道关了（<c>WaitAsync</c> 返回、<c>Closed</c> 令牌取消），
/// 紧接着的头几次写却照常返回，数据进了本地管道再被悄悄丢掉，通道流的写也就不抛 <see cref="IOException"/>。
/// 现在交给管道之前先问通道还收不收。
/// </para>
/// <para>
/// 问完「还收」到泵把数据发出去之间，通道仍可能关掉，那一次的数据照样丢 —— 与写进一条刚断开的套接字一样，
/// 这不是这里要消除的。要消除的是「调用方已经知道关了，写却还报成功」。
/// </para>
/// </remarks>
/// <param name="inner">stdin 管道的 writer。</param>
/// <param name="acceptsInput">通道还收不收 stdin 数据。</param>
internal sealed class StandardInputWriter(PipeWriter inner, Func<bool> acceptsInput) : PipeWriter
{
    private static readonly FlushResult Rejected = new(isCanceled: false, isCompleted: true);

    /// <inheritdoc />
    public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;

    /// <inheritdoc />
    public override long UnflushedBytes => inner.UnflushedBytes;

    /// <inheritdoc />
    public override void Advance(int bytes) => inner.Advance(bytes);

    /// <inheritdoc />
    public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

    /// <inheritdoc />
    public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

    /// <inheritdoc />
    public override void CancelPendingFlush() => inner.CancelPendingFlush();

    /// <inheritdoc />
    /// <remarks>完成 writer 就是 EOF（spec/05 §4.3），照常交给管道 —— 通道已经关了的话泵早已退出，什么也不会发。</remarks>
    public override void Complete(Exception? exception = null) => inner.Complete(exception);

    /// <inheritdoc />
    public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);

    /// <inheritdoc />
    /// <remarks>通道不收了：缓冲里没冲出去的那一截不再交给管道，留着随 writer 一起作废。</remarks>
    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) =>
        acceptsInput() ? inner.FlushAsync(cancellationToken) : ValueTask.FromResult(Rejected);

    /// <inheritdoc />
    public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default) =>
        acceptsInput() ? inner.WriteAsync(source, cancellationToken) : ValueTask.FromResult(Rejected);
}
