using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 被自动换行折成几行的长行,复制出来仍是一行(#517:<c>cat</c> 出来的一行公钥,
/// 选中复制后粘到文件里成了好几行)。折没折行记在 <c>TerminalRow.Wrapped</c> 上,
/// 复制、双击选词、导出缓冲区都得按它把物理行拼回逻辑行;显式换行则照旧断开。
/// </summary>
[TestClass]
[TestCategory("SoftWrapCopy")]
public sealed class SoftWrapCopyTests
{
    /// <summary>全程序集共用的 headless 会话(见 HeadlessTestSession:每类各起一个时,拆除会互相踩)。</summary>
    private static HeadlessUnitTestSession Session => HeadlessTestSession.Current;

    private const string Base64Chars =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    [TestMethod]
    public void Drag_AcrossASoftWrappedLine_CopiesItAsOneLine()
    {
        RunOnTerminal(
            (window, control) =>
            {
                int cols = control.Columns;
                string key = "ssh-rsa " + Base64(2 * cols);
                Feed(window, control, key + "\r\nnext");

                Drag(window, control, (0, 0), (3, 4));

                Assert.AreEqual(
                    key + "\nnext",
                    control.GetSelectedText(),
                    "自动换行折出来的行不该变成换行符;显式换行照旧保留。"
                );
            }
        );
    }

    [TestMethod]
    public void Drag_ALineThatExactlyFillsTheWidth_StillEndsWithANewline()
    {
        RunOnTerminal(
            (window, control) =>
            {
                // 恰好写满一行再显式换行:末列写完只是挂起换行,随后的 CR LF 不算自动换行。
                string full = new('x', control.Columns);
                Feed(window, control, full + "\r\nnext");

                Drag(window, control, (0, 0), (1, 4));

                Assert.AreEqual(full + "\nnext", control.GetSelectedText());
            }
        );
    }

    [TestMethod]
    public void Drag_KeepsASpaceThatFallsOnTheWrapBoundary()
    {
        RunOnTerminal(
            (window, control) =>
            {
                // 空格正好落在末列:它是这行中间的内容,「去除尾部空格」不能把它吃掉。
                string text = new string('a', control.Columns - 1) + " bbbb";
                Feed(window, control, text);

                Assert.IsTrue(control.TrimTrailingWhitespaceOnCopy, "默认开着去尾空格,本用例正要在它下面验证。");
                Drag(window, control, (0, 0), (1, 4));

                Assert.AreEqual(text, control.GetSelectedText());
            }
        );
    }

    [TestMethod]
    public void Drag_DoesNotCopyTheGapLeftByAWideCharThatWrapped()
    {
        RunOnTerminal(
            (window, control) =>
            {
                // 宽字符在末列放不下,整个挪到下一行,末列留一个没写过的空位 —— 那不是空格。
                string text = new string('a', control.Columns - 1) + "中文";
                Feed(window, control, text);

                Drag(window, control, (0, 0), (1, 4));

                Assert.AreEqual(text, control.GetSelectedText());
            }
        );
    }

    [TestMethod]
    public void AltDrag_OverSoftWrappedRows_StillBreaksEveryRow()
    {
        RunOnTerminal(
            (window, control) =>
            {
                int cols = control.Columns;
                string text = Base64(2 * cols);
                Feed(window, control, text);

                Drag(window, control, (0, 0), (1, 3), RawInputModifiers.Alt);

                // 块选是矩形:逐行取同一段列区间,不按逻辑行拼。
                Assert.AreEqual(
                    string.Concat(text.AsSpan(0, 3), "\n", text.AsSpan(cols, 3)),
                    control.GetSelectedText()
                );
            }
        );
    }

    [TestMethod]
    public void DoubleClick_SelectsAWordThatContinuesAcrossTheWrap()
    {
        RunOnTerminal(
            (window, control) =>
            {
                // 词从第 0 行起、跨过第 1 行、落到第 2 行;双击中间那行,两头都得找到。
                string word = Base64(2 * control.Columns);
                Feed(window, control, "id " + word + " tail");

                Point point = CellPoint(control, 1, 5);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                Assert.AreEqual(word, control.GetSelectedText());
            }
        );
    }

    [TestMethod]
    public void GetBufferText_JoinsSoftWrappedRows()
    {
        RunOnTerminal(
            (window, control) =>
            {
                string key = "ssh-rsa " + Base64(2 * control.Columns);
                Feed(window, control, key + "\r\nnext");

                // 「保存输出到文件」与复制同一规矩。
                Assert.AreEqual(
                    key + Environment.NewLine + "next" + Environment.NewLine,
                    control.GetBufferText()
                );
            }
        );
    }

    /// <summary>长度为 <paramref name="length" /> 的一串 Base64 字符(不含空格,整串算一个词)。</summary>
    private static string Base64(int length)
    {
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append(Base64Chars[(i * 7) % Base64Chars.Length]);
        }
        return sb.ToString();
    }

    /// <summary>在空的 headless 终端上跑一段交互;内容由用例在布局定下列宽之后再灌。</summary>
    private static void RunOnTerminal(Action<Window, VelaTerminalControl> body) =>
        Session
            .Dispatch(
                () =>
                {
                    var control = new VelaTerminalControl
                    {
                        CopyOnSelect = false, // headless 下不去碰剪贴板
                    };
                    var window = new Window
                    {
                        Width = 480,
                        Height = 320,
                        Content = control,
                    };
                    window.Show();
                    try
                    {
                        Dispatcher.UIThread.RunJobs();
                        window.CaptureRenderedFrame(); // 按窗口宽度定下列数

                        body(window, control);
                    }
                    finally
                    {
                        // 窗口必须关:留在共享 UI 线程上的渲染会拖到会话拆除时才跑(见 HeadlessTestSession)。
                        window.Close();
                    }
                    return true;
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();

    /// <summary>灌入文本并重绘一帧,刷新屏幕行映射。</summary>
    private static void Feed(Window window, VelaTerminalControl control, string text)
    {
        control.Feed(Encoding.UTF8.GetBytes(text));
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
    }

    private static void Drag(
        Window window,
        VelaTerminalControl control,
        (int Row, int Col) from,
        (int Row, int Col) to,
        RawInputModifiers modifiers = RawInputModifiers.None
    )
    {
        window.MouseDown(CellPoint(control, from.Row, from.Col), MouseButton.Left, modifiers);
        window.MouseMove(CellPoint(control, to.Row, to.Col), modifiers);
        window.MouseUp(CellPoint(control, to.Row, to.Col), MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>屏幕行/列的左上角坐标(略微内缩,避免落到相邻单元格)。</summary>
    private static Point CellPoint(VelaTerminalControl control, int row, int col) =>
        new(
            control.GutterForTest.TotalWidth + (col * control.CellWidthForTest) + 1,
            (row * control.CellHeightForTest) + 1
        );
}
