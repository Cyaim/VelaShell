using System.Text;
using NSubstitute;
using ReactiveUI.Builder;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Presentation.ViewModels;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// #555 多行快捷命令:原样写进终端的话,中间每个换行都等于按一次回车,前几行当场就执行了。
/// 改走括号粘贴;远端没开括号粘贴(那边仍会逐行执行)时先问一句。
/// </summary>
[TestClass]
[TestCategory("QuickCommands")]
public class QuickCommandMultilineFlowTests
{
    private const string Multiline = "cd /srv/app\ngit pull\nsystemctl restart app";

    static QuickCommandMultilineFlowTests()
    {
        try
        {
            RxAppBuilder
                .CreateReactiveUIBuilder()
                .WithMainThreadScheduler(CurrentThreadSequencer.Instance)
                .WithCoreServices()
                .BuildApp();
        }
        catch (InvalidOperationException)
        {
            // Already initialized
        }
    }

    [TestMethod]
    public void BracketedPasteTerminal_GetsTheWholeCommandAsOnePaste_WithoutAsking()
    {
        (MainWindowViewModel vm, ITerminalEmulator emulator) = Connected(bracketedPaste: true);
        bool asked = false;
        vm.QuickCommandMultilineConfirmer = _ =>
        {
            asked = true;
            return Task.FromResult(false);
        };

        Send(vm, Multiline);

        Assert.IsFalse(asked, "开着括号粘贴时整段进命令行、不会执行,不该打扰用户");
        emulator.Received(1).WritePasteInput(Multiline);
        emulator.DidNotReceive().WriteInput(Arg.Any<byte[]>());
    }

    [TestMethod]
    public void WithoutBracketedPaste_AsksFirst_AndCancellingSendsNothing()
    {
        (MainWindowViewModel vm, ITerminalEmulator emulator) = Connected(bracketedPaste: false);
        string? asked = null;
        bool focusRequested = false;
        vm.TerminalFocusRequested += (_, _) => focusRequested = true;
        vm.QuickCommandMultilineConfirmer = text =>
        {
            asked = text;
            return Task.FromResult(false);
        };

        Send(vm, Multiline);

        Assert.AreEqual(Multiline, asked);
        emulator.DidNotReceive().WritePasteInput(Arg.Any<string>());
        emulator.DidNotReceive().WriteInput(Arg.Any<byte[]>());
        Assert.IsFalse(focusRequested);
    }

    [TestMethod]
    public void WithoutBracketedPaste_ConfirmingSendsThePaste()
    {
        (MainWindowViewModel vm, ITerminalEmulator emulator) = Connected(bracketedPaste: false);
        vm.QuickCommandMultilineConfirmer = _ => Task.FromResult(true);

        Send(vm, Multiline);

        emulator.Received(1).WritePasteInput(Multiline);
    }

    /// <summary>关掉了「粘贴时确认多行内容」的人已经表示过不想被这类问题打断,快捷命令同样不问。</summary>
    [TestMethod]
    public void ConfirmationTurnedOffInSettings_SendsWithoutAsking()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        (MainWindowViewModel vm, ITerminalEmulator emulator) = Connected(bracketedPaste: false, settingsService);
        settingsService.SettingsSaved += Raise.Event<Action<AppSettings>>(
            new AppSettings { TerminalBehavior = new() { ConfirmMultilinePaste = false } }
        );
        bool asked = false;
        vm.QuickCommandMultilineConfirmer = _ =>
        {
            asked = true;
            return Task.FromResult(false);
        };

        Send(vm, Multiline);

        Assert.IsFalse(asked);
        emulator.Received(1).WritePasteInput(Multiline);
    }

    /// <summary>只有末尾带换行的单行命令不算多行:末尾的换行本来就去掉,照旧按键入发送,不问。</summary>
    [TestMethod]
    public void TrailingLineBreakOnly_IsStillTypedAsASingleLine()
    {
        (MainWindowViewModel vm, ITerminalEmulator emulator) = Connected(bracketedPaste: false);
        bool asked = false;
        vm.QuickCommandMultilineConfirmer = _ =>
        {
            asked = true;
            return Task.FromResult(false);
        };

        Send(vm, "ls -la\n");

        Assert.IsFalse(asked);
        emulator.Received(1).WriteInput(Arg.Is<byte[]>(bytes => Encoding.UTF8.GetString(bytes) == "ls -la"));
        emulator.DidNotReceive().WritePasteInput(Arg.Any<string>());
    }

    /// <summary>变量占位与多行可以同时出现:先问变量,替换后的整段再走括号粘贴。</summary>
    [TestMethod]
    public void MultilineWithPlaceholders_AsksForValues_ThenPastesTheRenderedText()
    {
        (MainWindowViewModel vm, ITerminalEmulator emulator) = Connected(bracketedPaste: true);
        vm.QuickCommandVariablePrompt = (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, string>?>(
            new Dictionary<string, string> { ["dir"] = "/srv/web" });

        Send(vm, "cd {{dir}}\ngit pull");

        emulator.Received(1).WritePasteInput("cd /srv/web\ngit pull");
    }

    private static (MainWindowViewModel Vm, ITerminalEmulator Emulator) Connected(
        bool bracketedPaste,
        ISettingsService? settingsService = null
    )
    {
        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            quickCommands: new QuickCommandsViewModel(repository)
        );
        ITerminalEmulator emulator = FakeTerminal.Emulator();
        emulator.IsBracketedPasteEnabled.Returns(bracketedPaste);
        var tab = new TerminalTabViewModel(emulator) { ConnectionStatus = SessionStatus.Connected };
        vm.Layout.AddDocument(new TerminalDocument(tab));
        return (vm, emulator);
    }

    private static void Send(MainWindowViewModel vm, string commandText) =>
        vm.Sidebar.QuickCommands!.SendCommand
            .Execute(new QuickCommandViewModel(new QuickCommand { Name = "multi", CommandText = commandText }))
            .Subscribe();
}
