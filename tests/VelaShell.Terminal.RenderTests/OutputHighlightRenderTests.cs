using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VelaShell.Core.Models;
using VelaShell.Core.OutputRules;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.RenderTests;

/// <summary>
/// 用户高亮规则的<b>像素级</b>回归:规则选的调色板色真的画到了格子上,而且选区底色仍压在它上面。
/// </summary>
/// <remarks>
/// 判据不写死颜色:同一行里用 <c>SGR 41</c>(程序显式设的调色板 1 号底色)画一格当参照 ——
/// 规则选「调色板 1 号」作背景,画出来就必须与那一格同色,换哪套配色方案都成立。
/// </remarks>
[TestClass]
[TestCategory("GlyphRendering")]
public class OutputHighlightRenderTests
{
    private static HeadlessUnitTestSession Session => SkiaTestSession.Current;

    private static void OnUi(Action body) =>
        Session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    [TestMethod]
    public void RuleBackground_IsThePaletteColor_AndSelectionStaysOnTop()
    {
        OnUi(() =>
        {
            using var control = new VelaTerminalControl { CopyOnSelect = false, CursorBlink = false };
            var window = new Window { Width = 640, Height = 360, Content = control };
            window.Show();
            try
            {
                RenderFrame(window);
                control.OutputRules = OutputRuleCompiler.Build(
                    [new OutputRule { Pattern = "HIT", Action = OutputRuleActions.Highlight, Background = 1 }], null, null);
                control.FeedRemote(Encoding.ASCII.GetBytes("\e[41mR\e[0m HIT miss"));

                uint reference = CellBackground(window, control, 0, 0);
                uint plain = CellBackground(window, control, 0, 6);
                Assert.AreNotEqual(plain, reference, "参照格要真有底色。");
                Assert.AreEqual(reference, CellBackground(window, control, 0, 2), "规则选的调色板 1 号底色要画在命中的格上。");
                Assert.AreEqual(reference, CellBackground(window, control, 0, 4));
                Assert.AreEqual(plain, CellBackground(window, control, 0, 5), "命中之外的格不受影响。");

                window.MouseDown(CellPoint(control, 0, 2), MouseButton.Left);
                window.MouseMove(CellPoint(control, 0, 5));
                window.MouseUp(CellPoint(control, 0, 5), MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.AreNotEqual(reference, CellBackground(window, control, 0, 3), "选区底色压在用户高亮之上,否则看不出选中了什么。");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static (uint[] Pixels, int Width) Render(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using WriteableBitmap bitmap = window.CaptureRenderedFrame()
            ?? throw new AssertFailedException("没有拿到渲染帧(Skia 后端未生效)。");
        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        int size = checked(width * height * 4);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, size, width * 4);
            uint[] pixels = new uint[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (uint)Marshal.ReadInt32(buffer, i * 4);
            }
            return (pixels, width);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void RenderFrame(Window window) => _ = Render(window);

    private static uint CellBackground(Window window, VelaTerminalControl control, int row, int col)
    {
        (uint[] pixels, int width) = Render(window);
        int left = (int)(control.GutterForTest.TotalWidth + col * control.CellWidthForTest);
        int top = (int)(row * control.CellHeightForTest);
        var cell = new List<uint>();
        for (int y = top + 1; y < top + control.CellHeightForTest - 1; y++)
        {
            for (int x = left + 1; x < left + control.CellWidthForTest - 1; x++)
            {
                cell.Add(pixels[y * width + x]);
            }
        }
        return cell.GroupBy(pixel => pixel).MaxBy(group => group.Count())!.Key;
    }

    private static Point CellPoint(VelaTerminalControl control, int row, int col) =>
        new(control.GutterForTest.TotalWidth + (col * control.CellWidthForTest) + 1, (row * control.CellHeightForTest) + 1);
}
