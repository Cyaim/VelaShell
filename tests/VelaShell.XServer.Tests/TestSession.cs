// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

// 用例逐条串行跑 —— 这就是 MSTest 的默认,本程序集一直如此;显式写出来是让 MSTEST0001 知道这是有意的。
[assembly: DoNotParallelize]

namespace VelaShell.XServer.Tests;

/// <summary>全程序集的测试会话:抬线程池下限。</summary>
/// <remarks>
/// <para>
/// <b>为什么抬下限。</b>CI 跑在 3 核的 runner 上(macos-latest),线程池的下限默认就是核数,
/// 多出来的线程靠「爬坡」注入 —— 每秒一两条。而这里每个服务端都有接受循环、执行循环,每条连接
/// 还有读端与写出端,一次跑起来就是一串续延;另外还有若干测试程序集同时跑。于是「谁的续延先排上
/// 队」全看运气(plan.md §50 记的那次就是这样)。下限抬上去之后,续延不必等注入,这一类抖动就没了。
/// </para>
/// <para>
/// <b>它改变什么、不改变什么。</b>只改变「线程够不够快到位」:用例该等的东西照等
/// (该等信号等信号,见 <c>RobustnessTests</c> 里那条大回复的用例),断言一条不变。
/// </para>
/// </remarks>
[TestClass]
public static class TestSession
{
    /// <summary>并行下限:3 核的 runner 上也给到 16 条;核多的机器按核数的 4 倍给。</summary>
    private static int Floor => Math.Max(16, Environment.ProcessorCount * 4);

    [AssemblyInitialize]
    public static void Init(TestContext _) =>
        // 工作线程与 I/O 完成端口都抬:内存管道、Unix 套接字与临时文件都会用到后者。
        ThreadPool.SetMinThreads(Floor, Floor);
}
