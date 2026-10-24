using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 环回 TCP 上的一台测试 SSH 主机:每个连进来的客户端走一遍真实的版本交换、KEX、口令认证,然后按剧本应答通道。
/// </summary>
/// <remarks>
/// 宿主侧的用例走 <c>SshConnectionAssembler</c> → <c>VelaSshClientWrapper</c> 的真实装配路径,
/// 拨号器是真的 TCP,所以对端得是一个真监听的端口,而不是内存传输。
/// 给了 <see cref="JumpTarget" /> 就是一台跳板:<c>direct-tcpip</c> 隧道里再跑一台目标服务端,
/// 目标名记进 <see cref="TunnelTargets" />。
/// </remarks>
internal sealed class LoopbackSshHost : IAsyncDisposable
{
    /// <summary>认证收的口令。</summary>
    public const string Password = "hunter2";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
    private readonly List<Task> _sessions = [];
    private readonly Task _accept;

    /// <param name="script">通道剧本(每条连接一份)。</param>
    /// <param name="options">服务端开关(版本串、算法、主机密钥)。</param>
    public LoopbackSshHost(TestChannelScript? script = null, TestSshServerOptions? options = null)
    {
        Script = script ?? new TestChannelScript { CloseAfterScript = false };
        Options = options;
        _listener.Start();
        _accept = AcceptLoopAsync();
    }

    /// <summary>监听的端口。</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>本机的通道剧本。</summary>
    public TestChannelScript Script { get; }

    /// <summary>本机的服务端开关。</summary>
    public TestSshServerOptions? Options { get; }

    /// <summary>当跳板时,隧道里那台目标的剧本与开关;<see langword="null" /> = 不肯转发。</summary>
    public (TestChannelScript Script, TestSshServerOptions? Options)? JumpTarget { get; init; }

    /// <summary>跳板收到的隧道目标(<c>host:port</c>)。</summary>
    public List<string> TunnelTargets { get; } = [];

    /// <summary>连进来过几次。</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    private int _accepted;

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Interlocked.Increment(ref _accepted);
                lock (_sessions)
                {
                    _sessions.Add(ServeClientAsync(client));
                }
            }
        }
        catch (Exception)
        {
            // 拆场:监听停了或被取消。
        }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        using (client)
        {
            TestChannelScript script = Script;
            if (JumpTarget is { } target)
            {
                script = script with
                {
                    TunnelHandler = (destination, input, output, ct) =>
                    {
                        lock (TunnelTargets)
                        {
                            TunnelTargets.Add(destination);
                        }
                        return ServeAsync(new DuplexPipeStream(input, output), target.Script, target.Options, ct);
                    },
                };
            }
            await ServeAsync(client.GetStream(), script, Options, _cts.Token);
        }
    }

    /// <summary>在一条流上当一回 SSH 服务端:握手、口令认证、按剧本应答通道,直到对端走掉。</summary>
    public static async Task ServeAsync(
        Stream stream, TestChannelScript script, TestSshServerOptions? options, CancellationToken cancellationToken)
    {
        try
        {
            await using TestSshServer server = new(stream, options);
            TestSshServerHandshake handshake = await server.HandshakeAsync(cancellationToken);
            TestAuthServer auth = new(server.Transport, handshake.ExchangeHash, new TestAuthPolicy { AcceptPassword = Password })
            {
                SshServer = server,
            };
            if (!await auth.RunAsync(cancellationToken))
            {
                return;
            }
            TestChannelServer channels = new(server.Transport, script);
            channels.AttachServer(server);
            await channels.RunAsync(cancellationToken);
        }
        catch (Exception)
        {
            // 客户端走了、协商失败、用例拆场 —— 服务端这一侧的收尾不是被测对象。
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        Task[] sessions;
        lock (_sessions)
        {
            sessions = [.. _sessions];
        }
        try
        {
            await Task.WhenAll([_accept, .. sessions]).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // 拆场。
        }
        _cts.Dispose();
    }

    /// <summary>一对管道拼成的双向流(隧道的服务端那一头)。</summary>
    private sealed class DuplexPipeStream(PipeReader input, PipeWriter output) : Stream
    {
        private readonly Stream _read = input.AsStream();
        private readonly Stream _write = output.AsStream();

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _read.ReadAsync(buffer, cancellationToken);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _write.WriteAsync(buffer, cancellationToken);
            await _write.FlushAsync(cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

        public override void Flush() => _write.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _read.Dispose();
                _write.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
