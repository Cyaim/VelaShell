using System.Runtime.CompilerServices;
using ReactiveUI.Builder;
using ReactiveUI.Primitives.Concurrency;

// 用例逐条串行跑 —— 这就是 MSTest 的默认,本程序集一直如此;显式写出来是让 MSTEST0001 知道这是有意的。
[assembly: DoNotParallelize]

namespace VelaShell.Presentation.Tests;

/// <summary>
/// ReactiveUI 的 WhenAnyValue / ReactiveCommand 需要先初始化容器,否则首次使用即抛
/// 「ReactiveUI has not been initialized」。测试里没有 UI 线程,主线程调度器用当前线程顺序器,
/// 这样 VM 里的订阅回调在断言之前就同步跑完。
/// </summary>
internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            RxAppBuilder.CreateReactiveUIBuilder()
                .WithMainThreadScheduler(CurrentThreadSequencer.Instance)
                .WithCoreServices()
                .BuildApp();
        }
        catch (InvalidOperationException)
        {
            // 已由其他路径初始化过。
        }
    }
}
