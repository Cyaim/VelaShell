using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 锁住流式 Markdown 每次渲染定稿后的 <c>RenderedTextProjection</c> 属性通知,
/// 使读取正文投影的调用方能观察首次渲染与后续追加;公式配色由渲染后端独立处理。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class MarkdownProjectionNotificationTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(
            typeof(MarkdownProjectionNotificationTests).Assembly);

    [TestMethod]
    public void RenderedTextProjection_RaisesPropertyChanged_OnEveryCommittedRender()
    {
        OnUi(async () =>
        {
            var text = new ObservableStringBuilder();
            var renderer = new MarkdownRenderer { MarkdownBuilder = text };
            int hits = 0;
            renderer.PropertyChanged += (_, e) =>
            {
                if (e.Property == MarkdownRenderer.RenderedTextProjectionProperty)
                {
                    hits++;
                }
            };

            var window = new Window { Width = 480, Height = 320, Content = renderer };
            window.Show();
            await PumpAsync();
            try
            {
                text.Append("# 标题\n\n正文一段。\n");
                await PumpAsync();
                Assert.IsGreaterThan(0, hits, "首次渲染定稿后应当有一次投影变更通知。");

                // 流式追加也必须通知正文投影的观察方。
                int afterFirst = hits;
                text.Append("\n$E = mc^2$\n\n又一段。\n");
                await PumpAsync();
                Assert.IsGreaterThan(
                    afterFirst, hits,
                    "后续追加渲染定稿后应当再发通知,以便观察方读取更新后的正文投影。"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void MathRendering_InheritsForeground_OnInitialRenderAndThemeChanges()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var panel = new ChatPanelView(context, new Configuration.AiSettingsStore(context));
            var text = new ObservableStringBuilder();
            var renderer = new MarkdownRenderer { MarkdownBuilder = text };
            var window = new Window { Width = 480, Height = 320, Content = renderer, RequestedThemeVariant = ThemeVariant.Dark };
            TextElement.SetForeground(renderer, Brushes.White);
            window.Show();
            try
            {
                text.Append("行内 $E=mc^2$。\n\n$$\\frac{1}{2}$$\n");
                await PumpAsync();
                var formulas = renderer.GetVisualDescendants().OfType<Control>()
                    .Where(control => control.GetType().FullName == "RaTeX.Avalonia.FormulaControl").ToList();
                Assert.IsGreaterThanOrEqualTo(2, formulas.Count, "行内与块级公式应生成 RaTeX 控件,而非退回纯文本。");
                foreach (Control formula in formulas)
                    Assert.AreEqual(Brushes.White, TextElement.GetForeground(formula));

                window.RequestedThemeVariant = ThemeVariant.Light;
                TextElement.SetForeground(renderer, Brushes.Black);
                await PumpAsync();
                foreach (Control formula in formulas)
                    Assert.AreEqual(Brushes.Black, TextElement.GetForeground(formula), "已有公式应跟随前景色变化。");

                window.RequestedThemeVariant = ThemeVariant.Dark;
                TextElement.SetForeground(renderer, Brushes.White);
                text.Append("\n新增 $a+b$。\n");
                await PumpAsync();
                formulas = [.. renderer.GetVisualDescendants().OfType<Control>().Where(control => control.GetType().FullName == "RaTeX.Avalonia.FormulaControl")];
                Assert.IsGreaterThanOrEqualTo(3, formulas.Count, "流式追加应渲染新公式。");
                foreach (Control formula in formulas)
                    Assert.AreEqual(Brushes.White, TextElement.GetForeground(formula));
            }
            finally { window.Close(); }
        });
    }

    /// <summary>渲染是后台解析 + UI 线程提交,跑几拍调度器让它落定。</summary>
    private static Task PumpAsync(int rounds = 60) => HeadlessPump.RunAsync(rounds);

    /// <summary>在 headless UI 线程上跑一段异步测试体(重载选择的坑见 ChatPanelViewUiTests.OnUi)。</summary>
    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
}
