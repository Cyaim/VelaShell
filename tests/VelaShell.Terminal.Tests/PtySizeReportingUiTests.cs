using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.Core.Ssh;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 控件报给 PTY 的尺寸带上像素:<c>window-change</c> 的像素字段原先恒为 0,
/// sixel / kitty 图形与按像素排版的 TUI 于是拿不到单元格尺寸。
/// 像素 = 单元格尺寸(DIP)× 行列 × 显示缩放,即网格本身的物理像素。
/// </summary>
[TestClass]
[TestCategory("TerminalGrid")]
public sealed class PtySizeReportingUiTests
{
    /// <summary>全程序集共用的 headless 会话(见 HeadlessTestSession)。</summary>
    private static HeadlessUnitTestSession Session => HeadlessTestSession.Current;

    [TestMethod]
    public void CurrentPtySize_CarriesTheGridsPixelSize()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = NewTerminal(900, 400);
            try
            {
                PtySize size = control.CurrentPtySize;

                Assert.AreEqual(control.Columns, size.Columns);
                Assert.AreEqual(control.Rows, size.Rows);
                Assert.AreEqual(Expected(control, control.Columns, control.Rows, 1), size);
                Assert.IsGreaterThan(0, size.PixelWidth, "像素宽度不能再是 0。");
                Assert.IsGreaterThan(0, size.PixelHeight, "像素高度不能再是 0。");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>验收口径:拖拽缩放之后报出去的尺寸,像素非零且与新网格对得上。</summary>
    [TestMethod]
    public void ResizingTheWindow_ReportsTheNewGridWithPixels()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = NewTerminal(900, 400);
            try
            {
                var reported = new List<PtySize>();
                control.PtySizeChanged += reported.Add;

                window.Width = 1300;
                window.Height = 600;
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame();

                Assert.IsNotEmpty(reported, "窗口变大后必须向 PTY 报新尺寸。");
                PtySize last = reported[^1];
                Assert.AreEqual((control.Columns, control.Rows), (last.Columns, last.Rows));
                Assert.AreEqual(Expected(control, last.Columns, last.Rows, 1), last);
                Assert.IsGreaterThan(0, last.PixelWidth);
                Assert.IsGreaterThan(0, last.PixelHeight);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 网格没变、只有缩放变了(拖到另一块显示器):行列相同也得补报一次,像素按物理像素算。
    /// </summary>
    [TestMethod]
    public void ScalingChangeAlone_ReportsPhysicalPixels()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = NewTerminal(900, 400);
            try
            {
                int cols = control.Columns;
                int rows = control.Rows;
                var reported = new List<PtySize>();
                control.PtySizeChanged += reported.Add;

                // headless 窗口恒为 1.0 缩放,只能注入;再让布局跑一遍,走「网格没变」的那条路。
                control.RenderScalingOverrideForTest = 1.5;
                control.InvalidateArrange();
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame();

                Assert.HasCount(1, reported, "行列没变、像素变了,应当正好补报一次。");
                Assert.AreEqual(Expected(control, cols, rows, 1.5), reported[0]);
                Assert.AreEqual(reported[0], control.CurrentPtySize);

                // 什么都没变时再跑一遍布局:不许重复报。
                control.InvalidateArrange();
                Dispatcher.UIThread.RunJobs();
                Assert.HasCount(1, reported, "尺寸没变就不该再报。");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static PtySize Expected(VelaTerminalControl control, int cols, int rows, double scale) =>
        new(
            cols,
            rows,
            (int)Math.Round(cols * control.CellWidthForTest * scale),
            (int)Math.Round(rows * control.CellHeightForTest * scale)
        );

    private static (VelaTerminalControl Control, Window Window) NewTerminal(int width, int height)
    {
        var control = new VelaTerminalControl
        {
            CopyOnSelect = false, // headless 下不去碰剪贴板
        };
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = control,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame(); // 填充单元格度量
        return (control, window);
    }

    private static void OnUi(Action body) =>
        Session
            .Dispatch(
                () =>
                {
                    body();
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();
}
