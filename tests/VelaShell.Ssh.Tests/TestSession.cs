// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.Ssh.Tests;

/// <summary>全程序集的测试会话:抬线程池下限。</summary>
/// <remarks>
/// <para>
/// <b>为什么抬下限。</b>CI 跑在 3 核的 runner 上(macos-latest),线程池的下限默认就是核数,
/// 多出来的线程靠「爬坡」注入 —— 每秒一两条。而这里的每条连接都要占上读端、写出端、执行循环
/// 好几个续延,另外还有若干测试程序集同时跑。于是「谁的续延先排上队」全看运气,运气不好的那条
/// 用例就红,而且红法不固定(plan.md §50 记过一次、§170 又是一次:连着两次红的不是同一条)。
/// 下限抬上去之后,续延不必等注入,这一类抖动就没了。
/// </para>
/// <para>
/// <b>它改变什么、不改变什么。</b>只改变「线程够不够快到位」,不改变用例的并发度 —— 那由
/// <c>test.runsettings</c> 里的 <c>Parallelize</c> 定,本套件另外压到了 2 个 worker。
/// 也不改变任何断言:用例该等的东西照等(该等信号等信号,见 <c>HostKeyRotationTests</c>)。
/// </para>
/// </remarks>
[TestClass]
public class TestSession
{
    /// <summary>并行下限:3 核的 runner 上也给到 16 条;核多的机器按核数的 4 倍给。</summary>
    private static int Floor => Math.Max(16, Environment.ProcessorCount * 4);

    [AssemblyInitialize]
    public static void Init(TestContext _) =>
        // 工作线程与 I/O 完成端口都抬:内存传输、管道与临时文件都会用到后者。
        ThreadPool.SetMinThreads(Floor, Floor);
}
