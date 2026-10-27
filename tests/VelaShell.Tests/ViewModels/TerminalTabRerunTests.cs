using System.Text;
using Avalonia.Input;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 侧栏菜单「重跑」的宿主一侧:<see cref="TerminalTabViewModel.TryRerunCommand" /> 发了什么、
/// 发的时候两个开关是不是关着。能不能重跑由终端控件判定(见 VelaShell.Terminal.Tests 的
/// <c>CommandBlockRerunUiTests</c>),这里只验发送。
/// </summary>
[TestClass]
[TestCategory("TerminalTab")]
public sealed class TerminalTabRerunTests
{
    private readonly ITerminalEmulator _terminal = FakeTerminal.Emulator();
    private readonly TerminalTabViewModel _vm;

    public TerminalTabRerunTests() => _vm = new(_terminal, Substitute.For<IShellStreamWrapper>());

    [TestMethod]
    public void Rerun_TypesTheCommandThenPressesEnter()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;

        Assert.IsTrue(_vm.TryRerunCommand("ls -la"));

        Received.InOrder(() =>
        {
            _terminal.WriteInput(Arg.Is<byte[]>(b => b.SequenceEqual(Encoding.UTF8.GetBytes("ls -la"))));
            _terminal.WriteKeyInput(Key.Enter, KeyModifiers.None);
        });
    }

    [TestMethod]
    public void Rerun_Multiline_GoesThroughThePastePath_ThenEnter()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;

        Assert.IsTrue(_vm.TryRerunCommand("for i in 1 2; do\necho $i\ndone"));

        Received.InOrder(() =>
        {
            _terminal.WritePasteInput("for i in 1 2; do\necho $i\ndone");
            _terminal.WriteKeyInput(Key.Enter, KeyModifiers.None);
        });
        _terminal.DidNotReceive().WriteInput(Arg.Any<byte[]>());
    }

    [TestMethod]
    public void Rerun_IsNotForwardedToTheSyncChannel_AndIsMarkedProgrammatic()
    {
        // 重跑点的是这一个终端历史里的一条命令,频道里别的标签此刻停在哪、该不该跑它都不知道;
        // 补全弹层也不该把它当用户键入弹出来。回车那一下同样在压制窗口里。
        _vm.ConnectionStatus = SessionStatus.Connected;
        var flags = new List<(bool Suppressed, bool Programmatic)>();
        _terminal.When(t => t.WriteInput(Arg.Any<byte[]>()))
            .Do(_ => flags.Add((_vm.IsSyncForwardSuppressed, _vm.IsProgrammaticInput)));
        _terminal.When(t => t.WriteKeyInput(Arg.Any<Key>(), Arg.Any<KeyModifiers>()))
            .Do(_ => flags.Add((_vm.IsSyncForwardSuppressed, _vm.IsProgrammaticInput)));

        _vm.TryRerunCommand("make");

        Assert.AreSequenceEqual([(true, true), (true, true)], flags);
        Assert.IsFalse(_vm.IsSyncForwardSuppressed, "发完要放开,否则用户接下来的键入也不转发了。");
        Assert.IsFalse(_vm.IsProgrammaticInput);
    }

    [TestMethod]
    public void Rerun_OnADisconnectedTab_SendsNothing()
    {
        Assert.IsFalse(_vm.TryRerunCommand("ls"));

        _terminal.DidNotReceive().WriteInput(Arg.Any<byte[]>());
        _terminal.DidNotReceive().WriteKeyInput(Arg.Any<Key>(), Arg.Any<KeyModifiers>());
    }

    [TestMethod]
    public void QuickCommands_StillDoNotPressEnter()
    {
        // 与快捷命令共用一条发送路径:那边「只发正文、由用户自己回车」的约定不能被顺手改掉。
        _vm.ConnectionStatus = SessionStatus.Connected;

        _vm.TrySendCommandText("kubectl get pods");

        _terminal.DidNotReceive().WriteKeyInput(Arg.Any<Key>(), Arg.Any<KeyModifiers>());
    }
}
