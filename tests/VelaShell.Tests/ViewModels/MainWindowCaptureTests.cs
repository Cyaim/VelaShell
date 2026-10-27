using System.Collections.Concurrent;
using System.Text;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Recording;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 主窗口把输出遮盖接在记录前面的那几行(<c>CaptureSink</c> / <c>StopSessionLogging</c>):
/// 开关开着才挂遮盖、关着原样记;停止记录时先把扣着的半个值遮完放进记录,再收尾 —— 顺序反了,那半个值就丢了。
/// </summary>
[TestClass]
public sealed class MainWindowCaptureTests
{
    [TestMethod]
    [DataRow(true, "DB_PASSWORD=***\r\nAPI_TOKEN=***", DisplayName = "开着遮盖:落库的是遮过的,扣着的半个值收尾时补进去")]
    [DataRow(false, "DB_PASSWORD=hunter2\r\nAPI_TOKEN=ab", DisplayName = "关着遮盖:原样落库")]
    public async Task Recording_IsMaskedOnlyWhenTheToggleIsOn_AndTheHeldValueLandsBeforeItCloses(bool mask, string expected)
    {
        byte[] output = "DB_PASSWORD=hunter2\r\nAPI_TOKEN=ab"u8.ToArray();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
        shell.CanRead.Returns(true);
        // 第一次读等测试放行(记录挂上之后)才交出那段输出;第二次读说明那一段已经走完记录旁路,之后一直挂着。
        shell.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = (CancellationToken)call[3];
                if (Interlocked.Increment(ref reads) == 1)
                {
                    await release.Task.WaitAsync(token);
                    output.CopyTo((byte[])call[0], (int)call[1]);
                    return output.Length;
                }
                consumed.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            });

        var recorded = new ConcurrentQueue<byte[]>();
        ISessionRecordingStore store = Substitute.For<ISessionRecordingStore>();
        store.AppendChunkAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<long>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                recorded.Enqueue((byte[])call[3]);
                return Task.CompletedTask;
            });

        var settings = new AppSettings();
        settings.Security.RecordProductionSessions = true;
        settings.Security.MaskSecretsInCaptures = mask;
        settings.General.SessionLogging = false;
        settings.General.AutoReconnect = false;
        settings.General.NotifyOnDisconnect = false;
        settings.General.SoundAlerts = false;
        settings.TerminalBehavior.ReportWorkingDirectory = false;
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(settings);

        var profile = new SessionProfile
        {
            Name = "web-01",
            Host = "web-01",
            Username = "ops",
            AuthMethod = AuthMethod.Password,
            Password = "pw"
        };
        var session = new SshSession
        {
            SessionId = Guid.NewGuid(),
            Status = SessionStatus.Connected,
            ConnectionInfo = new() { Host = profile.Host, Port = profile.Port, Username = profile.Username, AuthMethod = profile.AuthMethod }
        };
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        ssh.GetClient(session.SessionId).Returns(client);
        client.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096,
            Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()).Returns(shell);

        var vm = new MainWindowViewModel(workflow, ssh, () => FakeTerminal.Emulator(), settingsService: settingsService, recordingStore: store);
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.IsNotNull(tab);
        try
        {
            release.SetResult();
            await consumed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // 断开 → StopSessionLogging:先 Flush 遮盖(扣着的 "ab" 遮成 *** 进记录),再收尾录制。
            tab.MarkDisconnected();

            Assert.AreEqual(expected, Encoding.UTF8.GetString([.. recorded.SelectMany(chunk => chunk)]));
        }
        finally
        {
            await tab.DisposeAsync();
        }
    }
}
