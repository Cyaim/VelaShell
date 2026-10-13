using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// Real OpenSSH ACK regressions. The loopback relay understands no SSH packets: it only holds
/// server-to-client bytes and paces client-to-server bytes. Thus the peer, not a matching stub,
/// decides when WRITE succeeds. Credentials are opt-in environment variables, never constants.
/// </summary>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "Every network operation and relay has a separate bounded cancellation/close timeout.")]
[TestClass]
[DoNotParallelize]
public class SftpUploadProgressIntegrationTests
{
    private const int SuffixLength = 1024 * 1024;
    private const int BlockSize = 32 * 1024;
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("DockerIntegration")]
    [Timeout(60_000)]
    [DataRow(0)]
    [DataRow(4 * 1024 * 1024)]
    public async Task Upload_OnlyAcknowledgedBytes_AndFlushKeepsReporting(int prefixLength)
    {
        Settings settings = RequireSettings();
        await using var relay = new SlowRelay(settings.Port);
        await using Client client = await Client.ConnectAsync(settings, relay.Port);
        string path = NewPath();
        byte[] data = MakeData(prefixLength + SuffixLength);
        try
        {
            if (prefixLength > 0)
            {
                using var prefix = new MemoryStream(data, 0, prefixLength, writable: false);
                await client.Sftp.UploadAsync(prefix, path);
            }
            var clock = Stopwatch.StartNew();
            using var source = new ObservedSource(data, () => relay.StartSlowUpload());
            var samples = new ConcurrentQueue<Sample>();
            Task upload = client.Sftp.UploadAsync(source, path, prefixLength,
                bytes => samples.Enqueue(new(clock.Elapsed.TotalSeconds, bytes, source.Eof.IsCompleted)));
            await source.Eof.WaitAsync(TimeSpan.FromSeconds(15));
            double eofSeconds = clock.Elapsed.TotalSeconds;
            await Task.Delay(700);
            Sample[] held = samples.ToArray();
            Assert.IsFalse(upload.IsCompleted, "Source EOF must not complete an upload with held ACKs.");
            Assert.IsTrue(held.Length >= 4, "Copy and Flush callbacks must actually execute.");
            Assert.IsTrue(held.All(s => s.Bytes == (ulong)prefixLength),
                "Submitted but unacknowledged suffix bytes must not appear in progress.");
            Assert.IsTrue(held.All(s => s.Bytes < (ulong)data.Length * .9), "Held window must not reach 90%.");
            relay.ReleaseSlowly();
            await upload.WaitAsync(TimeSpan.FromSeconds(25));
            Sample[] all = samples.ToArray();
            AssertMonotonic(all, prefixLength, data.Length);
            ulong[] flushWatermarks = all.Where(s => s.AfterEof && s.Bytes > (ulong)prefixLength && s.Bytes < (ulong)data.Length)
                .Select(s => s.Bytes).Distinct().ToArray();
            Assert.IsTrue(flushWatermarks.Length >= 3, "Flush must report multiple increasing ACK watermarks, not jump to final.");
            Assert.AreEqual((ulong)data.Length, all[^1].Bytes);
            Assert.AreEqual(SuffixLength, source.BytesRead, "Resume must seek past prefix and read only the suffix.");
            Assert.IsTrue(all.All(s => s.Bytes == (ulong)data.Length || (s.Bytes - (ulong)prefixLength) % BlockSize == 0),
                "Real OpenSSH WRITE ACKs must advance in negotiated block units.");
            double duration = clock.Elapsed.TotalSeconds;
            relay.DisableSlowUpload();
            await VerifyDataAsync(client.Sftp, path, data);
            TestContext.WriteLine($"EXECUTED prefix={prefixLength} suffix={source.BytesRead} EOF={eofSeconds:F3}s total={duration:F3}s " +
                $"heldCallbacks={held.Length} heldMax={held.Max(s => s.Bytes)} flushIntermediate={flushWatermarks.Length} " +
                $"callbacks={all.Length} suffixRate={SuffixLength / duration / 1024:F1}KiB/s final={all[^1].Bytes}");
            TestContext.WriteLine("ACK timeline: " + string.Join(", ", all.Where(s => s.AfterEof)
                .GroupBy(s => s.Bytes).Select(g => $"{g.First().Seconds:F3}s={g.Key}")));
        }
        finally
        {
            relay.DisableSlowUpload();
            await DeleteQuietlyAsync(client.Sftp, path);
        }
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    [Timeout(45_000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Upload_CancellationIsNotReplacedByCloseFailure(bool breakConnection)
    {
        Settings settings = RequireSettings();
        await using var relay = new SlowRelay(settings.Port);
        await using Client client = await Client.ConnectAsync(settings, relay.Port);
        string path = NewPath();
        byte[] data = MakeData(SuffixLength);
        using var source = new ObservedSource(data, () => relay.StartSlowUpload());
        using var cancel = new CancellationTokenSource();
        var samples = new ConcurrentQueue<Sample>();
        Task upload = client.Sftp.UploadAsync(source, path,
            bytes => samples.Enqueue(new(0, bytes, source.Eof.IsCompleted)), cancel.Token);
        try
        {
            await source.Eof.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(500);
            Assert.IsFalse(upload.IsCompleted);
            Assert.IsTrue(samples.All(s => s.Bytes == 0));
            var clock = Stopwatch.StartNew();
            await cancel.CancelAsync();
            if (breakConnection)
            {
                relay.Abort();
            }
            else
            {
                relay.DisableSlowUpload();
            }
            OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(
                () => upload.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(cancel.Token, error.CancellationToken);
            Assert.IsTrue(upload.IsCanceled, "Upload task must be canceled, not faulted as a transfer interruption.");
            Assert.IsTrue(samples.All(s => s.Bytes < (ulong)data.Length), "Canceled upload must not report completion.");
            TestContext.WriteLine($"EXECUTED cancel breakConnection={breakConnection} task={upload.Status} " +
                $"close={clock.Elapsed.TotalSeconds:F3}s callbacks={samples.Count} max={samples.Max(s => s.Bytes)}");
        }
        finally
        {
            relay.DisableSlowUpload();
            await using Client cleanup = await Client.ConnectAsync(settings, settings.Port);
            await DeleteQuietlyAsync(cleanup.Sftp, path);
        }
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    [Timeout(60_000)]
    public async Task Upload_LinkFailureExposesExactAckPrefix_AndResumeRepairsSuffix()
    {
        Settings settings = RequireSettings();
        await using var relay = new SlowRelay(settings.Port);
        await using Client client = await Client.ConnectAsync(settings, relay.Port);
        string path = NewPath();
        byte[] data = MakeData(SuffixLength);
        using var source = new ObservedSource(data, () => relay.StartSlowUpload());
        var samples = new ConcurrentQueue<Sample>();
        Task upload = client.Sftp.UploadAsync(source, path, bytes => samples.Enqueue(new(0, bytes, source.Eof.IsCompleted)));
        try
        {
            await source.Eof.WaitAsync(TimeSpan.FromSeconds(15));
            relay.ReleaseSlowly();
            await EventuallyAsync(() => samples.Any(s => s.Bytes > 0 && s.Bytes < SuffixLength / 2));
            relay.HoldReplies();
            // Allow any bytes already forwarded and the 200ms Flush sampler to settle before cutting the link.
            await Task.Delay(500);
            ulong last = samples.Last().Bytes;
            Assert.IsTrue(last > 0 && last < SuffixLength);
            relay.Abort();
            VelaSftpTransferInterruptedException error = await Assert.ThrowsAsync<VelaSftpTransferInterruptedException>(
                () => upload.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual((long)last, error.DurableLength, "Interruption must expose the exact contiguous ACK watermark.");
            Assert.IsTrue(samples.All(s => s.Bytes < SuffixLength));
            Assert.IsTrue(upload.IsFaulted);
            await using Client recovery = await Client.ConnectAsync(settings, settings.Port);
            long? diskLength = (await recovery.Sftp.GetEntryAsync(path))?.Length;
            using var resumeSource = new ObservedSource(data, static () => { });
            var resumed = new ConcurrentQueue<ulong>();
            await recovery.Sftp.UploadAsync(resumeSource, path, error.DurableLength, resumed.Enqueue);
            Assert.AreEqual(data.Length - error.DurableLength, resumeSource.BytesRead);
            Assert.IsTrue(resumed.All(b => b >= (ulong)error.DurableLength));
            Assert.AreEqual((ulong)data.Length, resumed.Last());
            await VerifyDataAsync(recovery.Sftp, path, data);
            TestContext.WriteLine($"EXECUTED failure={error.GetType().Name} durable={error.DurableLength} " +
                $"lastCallback={last} remoteLength={diskLength} resumeRead={resumeSource.BytesRead} final={resumed.Last()}");
        }
        finally
        {
            relay.DisableSlowUpload();
            await using Client cleanup = await Client.ConnectAsync(settings, settings.Port);
            await DeleteQuietlyAsync(cleanup.Sftp, path);
        }
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    [Timeout(45_000)]
    public async Task Upload_FlushProgressCallbackFailureIsPreserved_AndConnectionRemainsUsable()
    {
        Settings settings = RequireSettings();
        await using var relay = new SlowRelay(settings.Port);
        await using Client client = await Client.ConnectAsync(settings, relay.Port);
        string path = NewPath();
        byte[] data = MakeData(SuffixLength);
        using var source = new ObservedSource(data, () => relay.StartSlowUpload());
        var expected = new InvalidOperationException("Controlled Flush progress callback failure.");
        var samples = new ConcurrentQueue<Sample>();
        int flushCallbacks = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            Task upload = client.Sftp.UploadAsync(source, path, bytes =>
            {
                bool afterEof = source.Eof.IsCompleted;
                samples.Enqueue(new(clock.Elapsed.TotalSeconds, bytes, afterEof));
                if (afterEof)
                {
                    Interlocked.Increment(ref flushCallbacks);
                    // Throw only from the 200ms Flush sampler, never from source-copy callbacks.
                    // Already submitted WRITEs must still drain and CLOSE successfully.
                    relay.DisableSlowUpload();
                    throw expected;
                }
            });
            InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
                () => upload.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreSame(expected, actual, "Flush cleanup must preserve the callback's original exception.");
            Assert.AreEqual(1, flushCallbacks);
            Assert.IsTrue(upload.IsFaulted);
            Assert.IsTrue(samples.All(s => s.Bytes == 0), "Held ACKs must not yield a final progress callback.");
            double failedAfter = clock.Elapsed.TotalSeconds;
            Assert.IsTrue(client.Sftp.IsConnected, "Callback failure must not poison the SFTP session.");
            await VerifyDataAsync(client.Sftp, path, data);
            TestContext.WriteLine($"EXECUTED callbackFailure={actual.GetType().Name} task={upload.Status} " +
                $"failedAfter={failedAfter:F3}s copyCallbacks={samples.Count - flushCallbacks} " +
                $"flushCallbacks={flushCallbacks} remoteHash=verified");
        }
        finally
        {
            relay.DisableSlowUpload();
            await DeleteQuietlyAsync(client.Sftp, path);
        }
    }

    private sealed record Settings(string User, string Password, int Port);
    private readonly record struct Sample(double Seconds, ulong Bytes, bool AfterEof);

    private static Settings RequireSettings()
    {
        string? user = Environment.GetEnvironmentVariable("VELA_ACK_TEST_USER");
        string? password = Environment.GetEnvironmentVariable("VELA_ACK_TEST_PASSWORD");
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        {
            Assert.Inconclusive("[SKIP] Set VELA_ACK_TEST_USER/PASSWORD for an isolated OpenSSH target; no default credentials are used.");
        }
        int port = int.TryParse(Environment.GetEnvironmentVariable("VELA_ACK_TEST_PORT"), out int configured) ? configured : 2233;
        return new(user!, password!, port);
    }

    private static string NewPath() => $"/tmp/vela-ack-{Guid.NewGuid():N}.bin";
    private static byte[] MakeData(int size) => RandomNumberGenerator.GetBytes(size);

    private static void AssertMonotonic(Sample[] samples, int prefix, int total)
    {
        ulong previous = (ulong)prefix;
        foreach (Sample sample in samples)
        {
            Assert.IsTrue(sample.Bytes >= previous && sample.Bytes <= (ulong)total);
            previous = sample.Bytes;
        }
    }

    private static async Task VerifyDataAsync(VelaSftpClientWrapper sftp, string path, byte[] expected)
    {
        using var actual = new MemoryStream();
        await sftp.DownloadAsync(path, actual);
        Assert.AreEqual(expected.Length, actual.Length);
        CollectionAssert.AreEqual(SHA256.HashData(expected), SHA256.HashData(actual.ToArray()), "Remote content, including resume prefix, must match.");
    }

    private static async Task DeleteQuietlyAsync(VelaSftpClientWrapper sftp, string path)
    {
        try { await sftp.DeleteFileAsync(path); }
        catch (Exception) { /* Dedicated temporary file; cleanup does not replace the test failure. */ }
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) { await Task.Delay(20, timeout.Token); }
    }

    private sealed class Client(SshConnection connection, VelaSftpClientWrapper sftp) : IAsyncDisposable
    {
        public VelaSftpClientWrapper Sftp { get; } = sftp;
        public static async Task<Client> ConnectAsync(Settings settings, int port)
        {
            SshConnection connection = await SshConnection.ConnectAsync(new SshConnectionOptions(settings.User, "127.0.0.1", port)
            {
                Credentials = [new PasswordCredential(settings.Password)],
                HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });
            var sftp = new VelaSftpClientWrapper(ct => SftpFileSystem.ConnectAsync(connection, new SftpOptions
            {
                BlockSize = BlockSize,
                MaxInFlight = 64,
                IsPipelineDepthAdaptive = false,
                CloseTimeout = TimeSpan.FromSeconds(3),
            }, ct));
            try
            {
                await sftp.ConnectAsync(CancellationToken.None);
                return new(connection, sftp);
            }
            catch
            {
                await sftp.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            await Sftp.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ObservedSource(byte[] data, Action firstRead) : MemoryStream(data, writable: false)
    {
        private bool _started;
        private readonly TaskCompletionSource _eof = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Eof => _eof.Task;
        public long BytesRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_started) { _started = true; firstRead(); }
            int read = Read(buffer.Span);
            BytesRead += read;
            if (read == 0) { _eof.TrySetResult(); }
            return ValueTask.FromResult(read);
        }
    }

    /// <summary>Only this test connection is paced; no container/network configuration is changed.</summary>
    private sealed class SlowRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(55));
        private readonly Task _run;
        private readonly int _targetPort;
        private TcpClient? _client;
        private TcpClient? _server;
        private volatile bool _slow;
        private volatile bool _held;
        public int Port { get; }
        public SlowRelay(int targetPort)
        {
            _targetPort = targetPort;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _run = RunAsync();
        }
        public void StartSlowUpload() { _held = true; _slow = true; }
        public void ReleaseSlowly() => _held = false;
        public void HoldReplies() => _held = true;
        public void DisableSlowUpload() { _slow = false; _held = false; }
        public void Abort() { _client?.Dispose(); _server?.Dispose(); }
        private async Task RunAsync()
        {
            try
            {
                _client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _server = new TcpClient();
                await _server.ConnectAsync(IPAddress.Loopback, _targetPort, _stop.Token);
                _client.NoDelay = _server.NoDelay = true;
                Task up = PumpAsync(_client.GetStream(), _server.GetStream(), downstream: false);
                Task down = PumpAsync(_server.GetStream(), _client.GetStream(), downstream: true);
                await Task.WhenAny(up, down);
                await _stop.CancelAsync();
                Abort();
                await Task.WhenAll(up, down);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // Normal relay teardown, including the deliberate failure scenario.
            }
        }
        private async Task PumpAsync(NetworkStream input, NetworkStream output, bool downstream)
        {
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await input.ReadAsync(buffer, _stop.Token);
                if (read == 0) { break; }
                int offset = 0;
                while (offset < read)
                {
                    while (downstream && _held) { await Task.Delay(10, _stop.Token); }
                    int count = _slow && downstream ? Math.Min(128, read - offset) : read - offset;
                    if (_slow)
                    {
                        await Task.Delay(downstream ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(count / (512d * 1024)), _stop.Token);
                    }
                    // Re-check hold after the pacing delay so a new hold never leaks the next piece.
                    while (downstream && _held) { await Task.Delay(10, _stop.Token); }
                    await output.WriteAsync(buffer.AsMemory(offset, count), _stop.Token);
                    offset += count;
                }
            }
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            Abort();
            await _run;
            _stop.Dispose();
        }
    }
}
