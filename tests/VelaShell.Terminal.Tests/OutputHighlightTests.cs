using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.Core.Models;
using VelaShell.Core.OutputRules;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 用户高亮规则接进渲染:逐列样式、规则之间的优先级、只在主屏;以及热路径不退化 ——
/// 帧间不变的行一条正则都不跑,规则集内容没变时不清缓存。
/// </summary>
[TestClass]
[TestCategory("OutputRules")]
public sealed class OutputHighlightTests
{
    private static OutputRule Rule(string pattern, int? fg = null, int? bg = null, bool bold = false) => new()
    {
        Name = pattern,
        Pattern = pattern,
        Action = OutputRuleActions.Highlight,
        Foreground = fg,
        Background = bg,
        Bold = bold,
    };

    private static void Run(Action<Window, VelaTerminalControl> body) =>
        HeadlessTestSession.Current.Dispatch(() =>
        {
            using var control = new VelaTerminalControl { CursorBlink = false };
            var window = new Window { Width = 640, Height = 360, Content = control };
            window.Show();
            try
            {
                Paint(window);
                body(window, control);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static void Paint(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
    }

    [TestMethod]
    public void MatchedColumns_CarryTheRuleStyle()
    {
        Run((_, control) =>
        {
            control.OutputRules = OutputRuleCompiler.Build([Rule("disk full", fg: 9, bg: 0, bold: true)], null, null);
            control.FeedRemote("warn: disk full now"u8);

            Assert.IsNull(control.UserHighlightAtForTest(0, 5));
            OutputHighlightStyle style = control.UserHighlightAtForTest(0, 6)!.Value;
            Assert.AreEqual(9, style.Foreground);
            Assert.AreEqual(0, style.Background);
            Assert.IsTrue(style.Bold);
            Assert.IsNotNull(control.UserHighlightAtForTest(0, 14));
            Assert.IsNull(control.UserHighlightAtForTest(0, 15));
        });
    }

    [TestMethod]
    public void WideCharacters_MapToTheirScreenColumns()
    {
        Run((_, control) =>
        {
            // 中文占两列:命中的是字符下标,画的是屏幕列,两者对不上就整体左移。
            control.OutputRules = OutputRuleCompiler.Build([Rule("错误", fg: 1)], null, null);
            control.FeedRemote(Encoding.UTF8.GetBytes("中文 错误 x"));

            // 中(0-1) 文(2-3) 空格(4) 错(5-6) 误(7-8) 空格(9) x(10);宽字符的尾格不单独着色(渲染跳过它)。
            Assert.IsNull(control.UserHighlightAtForTest(0, 4));
            Assert.IsNotNull(control.UserHighlightAtForTest(0, 5));
            Assert.IsNotNull(control.UserHighlightAtForTest(0, 7));
            Assert.IsNull(control.UserHighlightAtForTest(0, 9));
            Assert.IsNull(control.UserHighlightAtForTest(0, 10));
        });
    }

    [TestMethod]
    public void EarlierRule_WinsOnOverlap()
    {
        Run((_, control) =>
        {
            control.OutputRules = OutputRuleCompiler.Build([Rule("ERROR", fg: 1), Rule("ERR.*", fg: 3)], null, null);
            control.FeedRemote("ERROR here"u8);

            Assert.AreEqual(1, control.UserHighlightAtForTest(0, 0)!.Value.Foreground);
            Assert.AreEqual(3, control.UserHighlightAtForTest(0, 7)!.Value.Foreground, "没被前一条盖住的列归后一条。");
        });
    }

    [TestMethod]
    public void AlternateScreen_IsNotHighlighted()
    {
        Run((window, control) =>
        {
            control.OutputRules = OutputRuleCompiler.Build([Rule("x", fg: 1)], null, null);
            control.FeedRemote("\e[?1049hx"u8);
            Paint(window);
            int misses = control.HighlightMissCountForTest;
            Paint(window);
            Assert.AreEqual(misses, control.HighlightMissCountForTest, "备用屏连扫都不扫(与内置语义高亮同一条判据)。");
        });
    }

    [TestMethod]
    public void UnchangedRows_DoNotRerunTheRules()
    {
        Run((window, control) =>
        {
            List<OutputRule> rules = [.. Enumerable.Range(0, OutputRuleLimits.MaxRules).Select(i => Rule($"word{i}", fg: i % 16))];
            control.OutputRules = OutputRuleCompiler.Build(rules, null, null);
            control.FeedRemote(Encoding.UTF8.GetBytes(string.Join("\r\n", Enumerable.Range(0, 10).Select(i => $"line {i} word{i}"))));
            Paint(window);
            int afterFirstFrame = control.HighlightMissCountForTest;
            Assert.IsGreaterThan(0, afterFirstFrame);

            // 光标闪烁、选区、滚动条……一帧接一帧地重画,行没变就一条正则都不跑。
            for (int i = 0; i < 5; i++)
            {
                control.InvalidateTerminal();
                Paint(window);
            }
            Assert.AreEqual(afterFirstFrame, control.HighlightMissCountForTest);

            // 新来一行:只有这一行跑。
            control.FeedRemote("\r\nfresh word3"u8);
            Paint(window);
            Assert.AreEqual(afterFirstFrame + 1, control.HighlightMissCountForTest);
        });
    }

    [TestMethod]
    public void ReapplyingTheSameRules_KeepsTheCache_ChangingThemClearsIt()
    {
        Run((window, control) =>
        {
            List<OutputRule> rules = [Rule("alpha", fg: 2)];
            control.OutputRules = OutputRuleCompiler.Build(rules, null, null);
            control.FeedRemote("alpha beta"u8);
            Paint(window);
            int misses = control.HighlightMissCountForTest;

            // 每次保存设置、换主题,宿主都会对每个标签重设一遍规则集。
            control.OutputRules = OutputRuleCompiler.Build(rules, null, null);
            Paint(window);
            Assert.AreEqual(misses, control.HighlightMissCountForTest, "内容没变,不清缓存。");

            rules[0].Foreground = 4;
            control.OutputRules = OutputRuleCompiler.Build(rules, null, null);
            Paint(window);
            Assert.IsGreaterThan(misses, control.HighlightMissCountForTest, "换了规则,行要按新规则重算。");
            Assert.AreEqual(4, control.UserHighlightAtForTest(0, 0)!.Value.Foreground);
        });
    }

    [TestMethod]
    public void NoUserRules_LeaveTheBuiltInHighlightingAlone()
    {
        Run((_, control) =>
        {
            control.FeedRemote("error at https://example.com"u8);
            Assert.IsNull(control.UserHighlightAtForTest(0, 0));
            Assert.AreSame(OutputRuleSet.Empty, control.OutputRules);
        });
    }
}
