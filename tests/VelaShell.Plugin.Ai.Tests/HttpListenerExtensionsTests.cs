using System.Net;
using System.Net.Sockets;
using VelaShell.Plugin.Ai.Interop;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// <see cref="HttpListenerExtensions.AcceptAsync" />:接收循环的退出不押在「关监听会不会完成那次挂起的 accept」上。
/// </summary>
/// <remarks>
/// macOS / Linux 上的托管 <see cref="HttpListener" /> 关监听时唤醒不了恰好正要进来的那次 accept(见帮手的说明),
/// MCP 服务、企业微信通道与测试桩的收摊都曾因此可能永远等下去;CI 上 macOS 的 AI 插件 UI 测试整组挂住过一次。
/// Windows 走 HTTP.sys,没有那个窗口 —— 最后一条用例在 Windows 上只是照常跑一遍。
/// </remarks>
[TestClass]
public sealed class HttpListenerExtensionsTests
{
    [TestMethod]
    public async Task Accept_ReturnsTheIncomingRequest()
    {
        using HttpListener listener = StartListener(out string baseUrl);
        using var client = LoopbackClient();
        Task<HttpListenerContext?> accept = listener.AcceptAsync(CancellationToken.None);
        Task<HttpResponseMessage> request = client.GetAsync(baseUrl + "/hello");

        HttpListenerContext? context = await accept.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(context);
        Assert.AreEqual("/hello", context.Request.Url!.AbsolutePath);
        context.Response.StatusCode = 204;
        context.Response.Close();
        using HttpResponseMessage response = await request;
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    [TestMethod]
    public async Task Accept_ReturnsNullOnceStopIsRequested_EvenWhileTheListenerIsStillOpen()
    {
        using HttpListener listener = StartListener(out _);
        using var stop = new CancellationTokenSource();
        Task<HttpListenerContext?> accept = listener.AcceptAsync(stop.Token);
        Assert.IsFalse(accept.IsCompleted, "没有请求时应在等");

        await stop.CancelAsync();

        Assert.IsNull(await accept.WaitAsync(TimeSpan.FromSeconds(10)), "退出由令牌决定,不依赖关监听去唤醒");
        Assert.IsTrue(listener.IsListening);
    }

    [TestMethod]
    public async Task Accept_OnAClosedListener_ReturnsNull()
    {
        HttpListener listener = StartListener(out _);
        listener.Close();

        Assert.IsNull(await listener.AcceptAsync(CancellationToken.None));
    }

    /// <summary>
    /// 照测试桩收摊的顺序反复跑:挂住一个请求;收摊时先取消、再取消挂起的应答、再关监听 ——
    /// 循环被唤醒后马上回头 accept,与关监听赛跑。直接 await GetContextAsync 的写法在 Linux 上约每两三百轮挂住一轮(1400 轮 5 次);走帮手的 3000 轮 0 次。
    /// </summary>
    [TestMethod]
    public async Task StopRacingTheNextAccept_LoopAlwaysExits()
    {
        using var client = LoopbackClient();
        for (int round = 0; round < 200; round++)
        {
            HttpListener listener = StartListener(out string baseUrl);
            using var stop = new CancellationTokenSource();
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task loop = Task.Run(async () =>
            {
                while (await listener.AcceptAsync(stop.Token) is { } context)
                {
                    try
                    {
                        using (var reader = new StreamReader(context.Request.InputStream))
                        {
                            await reader.ReadToEndAsync(stop.Token);
                        }
                        received.TrySetResult();
                        await released.Task;
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException
                        || ex is OperationCanceledException && stop.IsCancellationRequested)
                    {
                        try { context.Response.Abort(); } catch (ObjectDisposedException) { }
                    }
                }
            });
            Task<HttpResponseMessage> request = client.PostAsync(baseUrl + "/v1/chat/completions", new StringContent("{}"));
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await stop.CancelAsync();
            released.TrySetCanceled();
            listener.Close();

            Task finished = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.AreSame(loop, finished, $"第 {round} 轮:收摊之后接收循环没有退出");
            await loop;
            try
            {
                (await request).Dispose();
            }
            catch (HttpRequestException)
            {
                // 挂住的那个请求随关监听断开
            }
        }
    }

    /// <summary>空闲端口可能落在 Windows HTTP.sys 的保留范围里:注册失败时换一个(与 SseStub 同一个处理)。</summary>
    private static HttpListener StartListener(out string baseUrl)
    {
        for (int attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                baseUrl = $"http://127.0.0.1:{port}";
                return listener;
            }
            catch (HttpListenerException ex) when (attempt < 9 && ex.ErrorCode is 5 or 183)
            {
                listener.Close();
            }
        }
    }

    /// <summary>本机回环不走代理(宿主给 DefaultProxy 装过东西)。</summary>
    private static HttpClient LoopbackClient() => new(new SocketsHttpHandler { UseProxy = false });
}
