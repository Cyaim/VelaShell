using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>AI 的呈现宽度归宿主管理,插件只负责内容和重复打开的生命周期。</summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class PanelWidthMemoryTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PanelWidthMemoryTests).Assembly);

    /// <summary>见 <c>ChatPanelViewUiTests.OnUi</c>:必须带返回值,否则测试体不会被等到底。</summary>
    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static Task PumpAsync(int rounds = 40) => HeadlessPump.RunAsync(rounds);

    /// <summary>激活插件、跑"打开助手"命令,并把面板真的挂进窗口。</summary>
    private static async Task<(FakePanel Panel, ChatPanelView View, Window Window)> OpenChatAsync(TestPluginContext context)
    {
        // 和真实宿主一样立刻造内容:插件就是在工厂里拿到那个视图引用的,
        // 惰性工厂会让它一直是 null,测出来的就不是真实路径了。
        context.FakeUi.CreateContentEagerly = true;
        var plugin = new AiPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);
        await context.RecordingCommands.RunAsync($"{context.PluginId}.chat");
        FakePanel panel = context.FakeUi.LastPanel;
        var view = (ChatPanelView)panel.CreateContent();
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        await PumpAsync();
        return (panel, view, window);
    }

    [TestMethod]
    public void OpeningChatAgain_ActivatesTheExistingPanel()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (FakePanel panel, ChatPanelView view, Window window) = await OpenChatAsync(context);
            try
            {
                await context.RecordingCommands.RunAsync($"{context.PluginId}.chat");

                Assert.HasCount(1, context.FakeUi.Panels);
                Assert.AreSame(panel, context.FakeUi.LastPanel);
                Assert.AreEqual(1, panel.ActivateCount);
            }
            finally
            {
                view.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void LegacyPluginWidth_IsNeitherAppliedNorUpdatedByTheAiPlugin()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await new AiSettingsStore(context).SaveAsync(new AiSettings { PanelWidthPercent = 55 });
            (FakePanel panel, ChatPanelView view, Window window) = await OpenChatAsync(context);
            try
            {
                Assert.AreNotEqual(0.55, panel.Options.PlacementRatio, "旧的插件百分比不再控制专属区");
                panel.RaisePlacementRatioChanged(0.42);
                view.GetControl<ComboBox>("ModeCombo").SelectedIndex = (int)ChatMode.Agent;
                await PumpAsync();
                AiSettings saved = await new AiSettingsStore(context).LoadAsync();
                Assert.AreEqual(55, saved.PanelWidthPercent, "宽度通知不得再写插件设置");
                Assert.AreEqual(ChatMode.Agent, saved.Mode);
            }
            finally { view.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void WindowAndDedicatedRegion_HaveIndependentReusableHandles()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (FakePanel panel, ChatPanelView view, Window window) = await OpenChatAsync(context);
            ChatPanelView? windowView = null;
            try
            {
                await context.RecordingCommands.RunAsync($"{context.PluginId}.chat-window");
                FakePanel separate = context.FakeUi.LastPanel;
                windowView = (ChatPanelView)separate.CreateContent();
                Assert.AreEqual(PanelDisplayMode.Window, separate.Options.DisplayMode);
                Assert.AreNotSame(view, windowView);
                await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => context.RecordingCommands.RunAsync($"{context.PluginId}.chat")));
                Assert.HasCount(2, context.FakeUi.Panels);
                Assert.AreEqual(3, panel.ActivateCount);
                Assert.AreEqual(0, separate.ActivateCount);
            }
            finally { windowView?.Detach(); view.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void NarrowPanel_LongServerNameKeepsAllTopBarActionsInsideThePanel()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (FakePanel _, ChatPanelView view, Window window) = await OpenChatAsync(context);
            try
            {
                ComboBox combo = view.GetControl<ComboBox>("SessionCombo");
                combo.ItemsSource = new[] { new SessionNavItem(new string('W', 180), null!) };
                combo.SelectedIndex = 0;
                foreach (double width in new[] { 800d, 300d })
                {
                    window.Width = width;
                    window.UpdateLayout();
                    await PumpAsync();
                    Assert.AreEqual(0, combo.TranslatePoint(default, view.GetControl<Grid>("TopBar"))!.Value.X, 0.1,
                        "服务器选择框在宽窄面板内均应贴左");
                }
                window.UpdateLayout();
                await PumpAsync();
                foreach (string name in new[] { "HistoryToggle", "NewChatButton", "ToolsButton", "SettingsButton" })
                {
                    Control button = view.GetControl<Control>(name);
                    Point position = button.TranslatePoint(default, view)!.Value;
                    Assert.IsTrue(position.X >= 0 && position.X + button.Bounds.Width <= view.Bounds.Width,
                        $"{name} 不得被长服务器名称挤出面板");
                }
                Assert.IsLessThan(180, combo.Bounds.Width);
            }
            finally { view.Detach(); window.Close(); }
        });
    }
}
