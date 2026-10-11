// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/06-sftp.md §5.3(被取消的 OPEN 迟到应答必须关)
//
// 等应答的人与收应答的循环谁拿到应答：两种先后顺序都要有人善后，而且只善后一次。
// 真实的竞态（取消与应答在同一刻到达）靠时序摆不出来，这里直接按两种顺序驱动状态机。

using System.Buffers;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Ssh.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public sealed class SftpPendingRequestTests
{
    private static SftpResponse Handle() =>
        new(SftpMessageType.Handle, 7, ArrayPool<byte>.Shared.Rent(8), 8);

    [TestMethod]
    public void 正常交付时应答交给等的人且不善后()
    {
        int lateCalls = 0;
        SftpRequestPipeline.PendingRequest pending = new(_ => lateCalls++);
        SftpResponse response = Handle();

        pending.Deliver(response);

        Assert.IsTrue(pending.Completion.Task.IsCompletedSuccessfully);
        Assert.AreSame(response, pending.Completion.Task.Result);
        Assert.AreEqual(0, lateCalls);
        response.Dispose();
    }

    [TestMethod]
    public void 先放弃后到应答时由收包一方善后()
    {
        int lateCalls = 0;
        SftpRequestPipeline.PendingRequest pending = new(_ => lateCalls++);

        pending.Abandon();
        pending.Deliver(Handle());

        Assert.AreEqual(1, lateCalls, "迟到的句柄没有被关掉");
        Assert.IsFalse(pending.Completion.Task.IsCompleted, "应答不该再交给已经走了的人");
    }

    /// <summary>
    /// 取消与应答同时到达：应答先交付到了任务上，等的人的 <c>WaitAsync</c> 却以取消结束。
    /// 曾经这个应答（带着服务端已经打开的句柄）就留在没人读的任务里 —— 句柄泄漏在服务端。
    /// </summary>
    [TestMethod]
    public void 应答已经交付而等的人放弃了_由放弃一方接手善后()
    {
        int lateCalls = 0;
        SftpRequestPipeline.PendingRequest pending = new(_ => lateCalls++);

        pending.Deliver(Handle());
        pending.Abandon();

        Assert.AreEqual(1, lateCalls, "已经交付、没人读的句柄没有被关掉");
    }

    [TestMethod]
    public void 收工时还在等的人拿到故障()
    {
        SftpRequestPipeline.PendingRequest pending = new(null);
        InvalidOperationException fault = new("通道断了");

        pending.Fail(fault);

        Assert.IsTrue(pending.Completion.Task.IsFaulted);
        Assert.AreSame(fault, pending.Completion.Task.Exception!.InnerException);
    }

    [TestMethod]
    public void 已经放弃的请求收工时不设异常()
    {
        // 等的人已经走了，没人会看这个任务 —— 设了异常就是一个未观察的任务异常，
        // 宿主据此写崩溃日志：一次断线让几十个被取消过的请求同时「崩溃」。
        SftpRequestPipeline.PendingRequest pending = new(null);

        pending.Abandon();
        pending.Fail(new InvalidOperationException("通道断了"));

        Assert.IsFalse(pending.Completion.Task.IsFaulted);
    }

    [TestMethod]
    public void 故障先到而等的人随后放弃_故障被看过不成为未观察的异常()
    {
        string marker = Guid.NewGuid().ToString("N");
        List<Exception> unobserved = [];
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerExceptions.Any(x => x.Message == marker))
            {
                lock (unobserved)
                {
                    unobserved.Add(e.Exception);
                }
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            FailThenAbandon(marker);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.IsEmpty(unobserved, "放弃的一方要接手看一眼这个故障");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void FailThenAbandon(string marker)
    {
        SftpRequestPipeline.PendingRequest pending = new(null);
        pending.Fail(new InvalidOperationException(marker));
        pending.Abandon();
    }

    [TestMethod]
    public void 重复放弃只善后一次()
    {
        int lateCalls = 0;
        SftpRequestPipeline.PendingRequest pending = new(_ => lateCalls++);

        pending.Abandon();
        pending.Abandon();
        pending.Deliver(Handle());

        Assert.AreEqual(1, lateCalls);
        Assert.IsTrue(pending.IsAbandoned);
    }
}
