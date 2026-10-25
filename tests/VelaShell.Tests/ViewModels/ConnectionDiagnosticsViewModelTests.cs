using System.Globalization;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 连接诊断窗口的步骤名、副标题与导出报告跟随界面语言。
/// </summary>
/// <remarks>
/// 这几处原先写死中文,切到英文照样是中文 —— 连导出的报告文件名都是「诊断报告-…」。
/// </remarks>
[TestClass]
[TestCategory("i18n")]
public class ConnectionDiagnosticsViewModelTests
{
    [TestMethod]
    public void EnglishUi_ShowsEnglishStepsSummaryAndReport()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new("en");
            var profile = new SessionProfile { Name = "web", Host = "web.example.com", Username = "root", Port = 22 };
            var vm = new ConnectionDiagnosticsViewModel(profile, Substitute.For<IConnectionDiagnosticsService>());

            Assert.AreEqual("1. DNS Resolution", vm.Steps[0].DisplayName);
            Assert.Contains("root@web.example.com:22", vm.TargetSummary);
            Assert.StartsWith("diagnostics-web-", vm.SuggestedReportFileName);
            string report = vm.BuildReportText();
            Assert.StartsWith("VelaShell Connection Diagnostics Report", report);
            Assert.Contains("[Not run]", report);
            Assert.DoesNotContain(c => c is >= '一' and <= '鿿', report, "英文界面下导出的报告里不该有中文:\n" + report);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    /// <summary>
    /// 协议轨迹:一行一个报文(类型、方向、长度、序号、通道),跳板链上换跳时标出那一跳;
    /// 我们发出的认证报文长度是「—」;复制与导出报告里是同一段纯文本;超过上限的说一声。
    /// </summary>
    [TestMethod]
    public async Task ProtocolTrace_ListsMetadataPerHop_AndGoesIntoCopyAndReport()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new("en");
            var profile = new SessionProfile { Name = "web", Host = "t", Username = "joe", Port = 22 };
            IConnectionDiagnosticsService service = Substitute.For<IConnectionDiagnosticsService>();
            service.DiagnoseAsync(Arg.Any<SessionProfile>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>())
                .Returns(new DiagnosticReport
                {
                    Steps = [],
                    PacketTrace =
                    [
                        new(TimeSpan.FromMilliseconds(3), "jumper@j:22", SshPacketDirection.Sent, 20, 1180, 0, null),
                        new(TimeSpan.FromMilliseconds(40), "jumper@j:22", SshPacketDirection.Sent, 50, null, 4, null),
                        new(TimeSpan.FromMilliseconds(90), "joe@t:22", SshPacketDirection.Received, 94, 64, 9, 2),
                    ],
                    PacketTraceDropped = 3,
                });
            var vm = new ConnectionDiagnosticsViewModel(profile, service);
            string? copied = null;
            vm.CopyToClipboard = text =>
            {
                copied = text;
                return Task.CompletedTask;
            };

            await vm.RunCommand.Execute().FirstAsync();

            Assert.IsTrue(vm.HasPacketTrace);
            Assert.AreEqual("Protocol trace · 3", vm.PacketTraceTabTitle);
            Assert.AreSequenceEqual(["jumper@j:22", null, "joe@t:22"], [.. vm.PacketTrace.Select(static r => r.HopHeader)]);
            Assert.AreEqual("KEXINIT (20)", vm.PacketTrace[0].Message);
            Assert.AreEqual("1,180 B", vm.PacketTrace[0].Length);
            Assert.AreEqual("—", vm.PacketTrace[1].Length, "我们发出的认证报文不显示长度");
            Assert.AreEqual("ch 2", vm.PacketTrace[2].Channel);
            Assert.StartsWith("←", vm.PacketTrace[2].Direction);
            Assert.AreEqual("key exchange method (31)", PacketTraceRowViewModel.Describe(31));
            Assert.IsNotNull(vm.PacketTraceDroppedText);

            await vm.CopyPacketTraceCommand.Execute().FirstAsync();
            Assert.AreEqual(vm.BuildPacketTraceText(), copied);
            Assert.Contains("[jumper@j:22]", copied!);
            Assert.Contains("USERAUTH_REQUEST (50)", copied!);
            Assert.Contains(copied!, vm.BuildReportText(), "导出报告附上同一段轨迹");
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}
