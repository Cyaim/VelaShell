using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Services;
using VelaShell.Terminal.Rendering;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 命令补全弹层贴着光标行弹出:下面放得下就落在光标行下沿之下,放不下(提示符在末行)翻到上沿之上 ——
/// 两种情况都留一道缝、都不压进光标行。
/// </summary>
/// <remarks>
/// 缝曾经用 <c>VerticalOffset="2"</c> 做:Avalonia 的定位器把弹层翻到上方时偏移量不跟着反向,
/// 翻上去的弹层就往光标行里压 2px(plan.md §222 记下的收尾)。改为照共享密码弹层的做法,缝做在锚点矩形上。
/// </remarks>
[TestClass]
[TestCategory("CommandSuggest")]
public sealed class CommandSuggestPopupPlacementUiTests
{
    private static HeadlessUnitTestSession Session =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(CommandSuggestPopupPlacementUiTests).Assembly);

    [TestMethod]
    public Task Popup_WithRoomBelow_SitsJustUnderTheCursorRow() => Session.RunOnUiAsync(async () =>
    {
        await using Harness harness = await Harness.ShowAsync();
        harness.Control.Feed("$ "u8.ToArray());

        Rect popup = await harness.OpenWithAltEnterAsync();

        Rect cursor = harness.CursorInWindow();
        Assert.IsTrue(popup.Top >= cursor.Bottom + 1 && popup.Top <= cursor.Bottom + 4,
            $"弹层应紧贴光标行下沿、留一道缝:popup={popup} cursor={cursor}");
    });

    [TestMethod]
    public Task Popup_OnTheLastRow_FlipsAboveWithoutCoveringTheCursorRow() => Session.RunOnUiAsync(async () =>
    {
        await using Harness harness = await Harness.ShowAsync();
        VelaTerminalControl control = harness.Control;
        // 输出刷满一屏后提示符落在末行(最大化时的常态):下面放不下,定位器把弹层翻到上面去。
        control.Feed(Encoding.UTF8.GetBytes(new string('\n', control.Rows * 2) + "$ "));
        Assert.AreEqual(control.Rows - 1, control.CursorRow, "前提:提示符在末行。");

        Rect popup = await harness.OpenWithAltEnterAsync();

        Rect cursor = harness.CursorInWindow();
        Assert.IsTrue(popup.Bottom <= cursor.Top - 1 && popup.Bottom >= cursor.Top - 4,
            $"翻上去的弹层应止于光标行上沿之上、留一道缝,不能压进光标行:popup={popup} cursor={cursor}");
    });

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Window _window;
        private readonly TerminalTabViewModel _tab;

        private Harness(Window window, TerminalTabView view, TerminalTabViewModel tab, VelaTerminalControl control)
        {
            _window = window;
            View = view;
            _tab = tab;
            Control = control;
        }

        public TerminalTabView View { get; }

        public VelaTerminalControl Control { get; }

        public static Task<Harness> ShowAsync()
        {
            var control = new VelaTerminalControl();
            IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
            shell.CanRead.Returns(false);
            shell.CanWrite.Returns(true);
            shell.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            var history = new CommandHistoryService(null);
            history.Record("git status");
            history.Record("docker ps -a");
            history.Record("journalctl -fu nginx");
            var tab = new TerminalTabViewModel(control, shell)
            {
                ConnectionStatus = SessionStatus.Connected,
                SuggestionProvider = new CommandSuggestionProvider(history, null),
            };
            var main = new MainWindowViewModel();
            typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.ActiveTerminalTab))!.SetValue(main, tab);
            var view = new TerminalTabView { DataContext = tab };
            var window = new Window { Width = 800, Height = 480, DataContext = main, Content = view };
            window.Show();
            Paint(window);
            control.Focus();
            Paint(window);
            return Task.FromResult(new Harness(window, view, tab, control));
        }

        /// <summary>Alt+Enter 召出全量补全(空输入 = 快捷命令 + 最近历史),等弹层开出来,返回它在窗口坐标里的位置。</summary>
        public async Task<Rect> OpenWithAltEnterAsync()
        {
            Paint(_window);
            _window.KeyPress(Key.Enter, RawInputModifiers.Alt, PhysicalKey.Enter, null);
            _window.KeyRelease(Key.Enter, RawInputModifiers.Alt, PhysicalKey.Enter, null);
            for (int i = 0; i < 100 && !View.SuggestPopupOpenForTest; i++)
            {
                await Task.Delay(10);
                Paint(_window);
            }
            Assert.IsTrue(View.SuggestPopupOpenForTest, "Alt+Enter 应召出补全弹层。");
            Paint(_window);
            Control child = View.FindControl<Popup>("SuggestPopup")!.Child!;
            return new Rect(child.TranslatePoint(default, _window)!.Value, child.Bounds.Size);
        }

        /// <summary>光标格在窗口坐标里的矩形。</summary>
        public Rect CursorInWindow()
        {
            Rect cursor = Control.GetCursorRect();
            return new Rect(Control.TranslatePoint(cursor.TopLeft, _window)!.Value, cursor.Size);
        }

        private static void Paint(Window window)
        {
            Dispatcher.UIThread.RunJobs();
            using WriteableBitmap? frame = window.CaptureRenderedFrame();
        }

        public async ValueTask DisposeAsync()
        {
            _window.Close();
            await _tab.DisposeAsync();
            Control.Dispose();
        }
    }
}
