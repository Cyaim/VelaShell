using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Controls;
using VelaShell.Services;
// VelaShell.Services 里另有一个同名的 KeyModifiers(KeyboardShortcutService 的平台无关枚举)。
using KeyModifiers = Avalonia.Input.KeyModifiers;

namespace VelaShell.Tests.Views;

/// <summary>
/// 键位表落到真实的 Avalonia 按键分发上(#551):登记了的手势先于焦点控件被吃掉,
/// 解绑或改走的手势原样到达焦点控件(在主窗口里就是终端)。
/// </summary>
/// <remarks>
/// 用一个普通窗口 + 一个可聚焦的探针控件代替主窗口与终端:要验证的正是 KeyBindings 与焦点控件之间
/// 的先后,与终端本身无关。KeyBindings 先于隧道 / 冒泡处理器匹配这件事,修 #551 时实测过。
/// </remarks>
[TestClass]
[TestCategory("Keyboard")]
public sealed class ShortcutKeyBindingsUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ShortcutKeyBindingsUiTests).Assembly);

    [TestMethod]
    public void BoundGestures_RunTheCommand_UnboundOnesReachTheFocusedControl()
    {
        _session.Dispatch(() =>
        {
            var executed = new List<string>();
            var probe = new KeyProbe();
            var window = new Window { Content = probe };
            var keymap = new ShortcutKeymap(new Dictionary<string, string>
            {
                ["app.palette"] = "Ctrl+Shift+P",
                ["session.close"] = "",
            }, isMacOS: false);
            ShortcutKeyBindings.Apply(window.KeyBindings, keymap, new RecordingCommand(executed));
            window.Show();
            probe.Focus();
            try
            {
                Press(window, Key.P, PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift);
                Press(window, Key.P, PhysicalKey.P, RawInputModifiers.Control);
                Press(window, Key.W, PhysicalKey.W, RawInputModifiers.Control);
                Press(window, Key.B, PhysicalKey.B, RawInputModifiers.Control);

                Assert.AreSequenceEqual(["app.palette", "view.sidebar"], executed, "改过的 Ctrl+Shift+P 与出厂的 Ctrl+B 照常触发。");
                Assert.AreSequenceEqual([Key.P, Key.W], probe.Received, "改走的 Ctrl+P 与解绑的 Ctrl+W 原样到达焦点控件(终端)。");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>整表替换:重新登记时旧的绑定一条不剩,不会出现改过的键位与出厂键位同时生效。</summary>
    [TestMethod]
    public void Apply_ReplacesThePreviousBindings()
    {
        _session.Dispatch(() =>
        {
            var window = new Window();
            var command = new RecordingCommand([]);
            ShortcutKeyBindings.Apply(window.KeyBindings, ShortcutKeymap.Default, command);
            int defaults = window.KeyBindings.Count;

            ShortcutKeyBindings.Apply(window.KeyBindings,
                                      new(new Dictionary<string, string> { ["session.close"] = "" }, isMacOS: false), command);

            Assert.HasCount(defaults - 1, window.KeyBindings);
            Assert.DoesNotContain(binding => binding.Gesture is { Key: Key.W, KeyModifiers: KeyModifiers.Control }, window.KeyBindings);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 录键框:只按修饰键不交差;真正的键连同修饰键一起交出去;Tab 也照录(不移焦);
    /// 不带修饰键的 Esc 是取消。每一下都标 Handled,设置窗口的 Esc 关窗不会被触发。
    /// </summary>
    [TestMethod]
    public void Recorder_CapturesCombinations_AndEscCancels()
    {
        _session.Dispatch(() =>
        {
            var captured = new List<KeyGesture>();
            int cancelled = 0;
            var recorder = new ShortcutRecorder { Prompt = "…" };
            recorder.GestureCaptured += (_, gesture) => captured.Add(gesture);
            recorder.Cancelled += (_, _) => cancelled++;
            bool reachedWindow = false;
            var window = new Window { Content = recorder };
            window.KeyDown += (_, _) => reachedWindow = true;
            window.Show();
            recorder.Focus();
            try
            {
                window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
                Assert.IsEmpty(captured, "只按 Ctrl 还不算一个键位。");

                Press(window, Key.P, PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift);
                Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Control);
                Assert.IsTrue(recorder.IsFocused, "Ctrl+Tab 被录下来,而不是把焦点移走。");
                Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);

                Assert.HasCount(2, captured);
                Assert.IsTrue(ShortcutGestures.AreSame(new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift), captured[0]));
                Assert.IsTrue(ShortcutGestures.AreSame(new KeyGesture(Key.Tab, KeyModifiers.Control), captured[1]));
                Assert.AreEqual(1, cancelled);
                Assert.IsFalse(reachedWindow, "录键时的按键不该冒泡到窗口(设置窗口的 Esc 会关窗)。");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void Press(Window window, Key key, PhysicalKey physical, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, physical, null);
        window.KeyRelease(key, modifiers, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>代替终端的焦点控件:记下到达它的每一个按键。</summary>
    private sealed class KeyProbe : Control
    {
        public KeyProbe() => Focusable = true;

        public List<Key> Received { get; } = [];

        protected override void OnKeyDown(KeyEventArgs e)
        {
            Received.Add(e.Key);
            e.Handled = true;
        }
    }

    private sealed class RecordingCommand(List<string> executed) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => executed.Add((string)parameter!);
    }
}
