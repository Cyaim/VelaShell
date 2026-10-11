using System.Net;

namespace VelaShell.Plugin.Ai.Interop;

/// <summary><see cref="HttpListener" /> 接收循环共用的一步:接下一个请求,或者在停机时干净地退出。</summary>
internal static class HttpListenerExtensions
{
    /// <summary>
    /// 等下一个请求;<paramref name="cancellationToken" /> 取消了或监听已经关掉时返回 <see langword="null" />。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>停机不能只靠关监听来唤醒挂起的 <see cref="HttpListener.GetContextAsync" />。</b>
    /// macOS / Linux 上的托管实现里,取请求那一步只看一眼「还在监听」就把等待项排进队列(不拿监听的内部锁);
    /// 关监听则是先把队列里<b>已有</b>的等待项以异常结束、清空队列,最后才把状态改成「已关」。
    /// 恰好夹在这两步之间进来的那次取请求排在清空之后,<b>永远不会完成</b> ——
    /// 等接收循环退出的人(停 MCP 服务、停企业微信通道、测试桩的 Dispose)就跟着永远等下去。
    /// 停机时先取消、循环被唤醒后马上回头再取一次请求,而另一边正在关监听:撞上这个窗口并不难
    /// (CI 上 macOS 的 AI 插件 UI 测试整组挂住过;在 Linux 上照测试桩的收摊顺序跑 1400 轮,挂住 5 轮)。
    /// Windows 走 HTTP.sys,没有这个窗口。
    ///
    /// <para>所以等待本身要听停机令牌,不把退出押在监听会不会完成那次取请求上。
    /// 放弃掉的那次取请求要么随关监听以异常收场 —— 在这里观察掉,不留成无人观察的任务异常;
    /// 要么永远挂在那个已关的监听上 —— 不再有人等它。</para>
    /// </remarks>
    public static async Task<HttpListenerContext?> AcceptAsync(this HttpListener listener, CancellationToken cancellationToken)
    {
        Task<HttpListenerContext> accept;
        try
        {
            accept = listener.GetContextAsync();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException
            || ex is InvalidOperationException && !listener.IsListening)
        {
            // 监听已经关了。Windows 上关到一半(已停、还没标成已释放)时报的是「先调 Start()」那个 InvalidOperationException。
            return null;
        }

        try
        {
            return await accept.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = accept.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return null;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            return null; // 监听被关掉了
        }
    }
}
