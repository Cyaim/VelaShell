using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 输入框旁边那个思考档位下拉。它的全部意义在"临时"两个字上:
/// 改了要对下一条消息生效,但<b>不能</b>动模型配置里保存的那个值。
/// </summary>
public sealed partial class ChatPanelViewUiTests
{
    /// <summary>一个供应商挂两个模型,用来验"换模型会不会把上一个的临时档位带过去"。</summary>
    private static AiProvider TwoModels(ReasoningLevel first, ReasoningLevel second)
        => new()
        {
            ModelsExpanded = true,
            Name = "stub",
            BaseUrl = "http://127.0.0.1:1/v1",
            DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
            Models =
            [
                new AiModelConfig { Model = "m1", Reasoning = first },
                new AiModelConfig { Model = "m2", Reasoning = second }
            ]
        };

    private static async Task<(Window Window, ChatPanelView Panel, ComboBox Combo)> WithReasoningPickerAsync(
        TestPluginContext context, AiProvider provider)
    {
        await new AiSettingsStore(context).SaveAsync(new AiSettings
        {
            Providers = [provider],
            ActiveModelId = provider.Models[0].Id,
            SuggestFollowUps = false
        });
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        return (window, panel, Find<ComboBox>(panel, "ReasoningCombo"));
    }

    [TestMethod]
    [DataRow(ReasoningLevel.High)]
    [DataRow(ReasoningLevel.ExtraHigh)]
    [DataRow(ReasoningLevel.Max)]
    public void ReasoningPicker_StartsOnTheLevelTheModelIsConfiguredWith(ReasoningLevel level)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, TwoModels(level, ReasoningLevel.Default));
            try
            {
                Assert.AreEqual((int)level, combo.SelectedIndex);
                Assert.Contains(new Loc("en")[level switch
                {
                    ReasoningLevel.ExtraHigh => "ReasoningExtraHigh",
                    ReasoningLevel.Max => "ReasoningMax",
                    _ => "ReasoningHigh"
                }], Find<TextBlock>(panel, "ReasoningSummary").Text!);
                Assert.HasCount(7, combo.ItemsSource!);
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 这一条是整个功能的要害:在工具条上改档位,<b>不能</b>写回模型配置。
    /// 写回去的话,下次打开设置页会发现档位莫名其妙换了,而用户根本不记得动过设置。
    /// </summary>
    [TestMethod]
    [DataRow(ReasoningLevel.Low)]
    [DataRow(ReasoningLevel.ExtraHigh)]
    [DataRow(ReasoningLevel.Max)]
    public void PickingALevel_DoesNotWriteBackToTheSavedModelSetting(ReasoningLevel level)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, TwoModels(ReasoningLevel.High, ReasoningLevel.Default));
            try
            {
                combo.SelectedIndex = (int)level;
                await PumpAsync(10);

                AiSettings saved = await store.LoadAsync();
                Assert.AreEqual(ReasoningLevel.High, saved.Providers[0].Models[0].Reasoning,
                    "工具条上的选择是临时的,不该改动保存的配置");
                Assert.AreEqual((int)level, combo.SelectedIndex, "但界面上得停在选中的那一档");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 选回模型自己那一档 = 取消覆盖,而不是"覆盖成同一个值"——
    /// 否则芯片会一直亮着,而它本该只在"这一轮不一样"时才亮。
    /// </summary>
    [TestMethod]
    public void PickingTheSavedLevelAgain_ClearsTheOverride()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, TwoModels(ReasoningLevel.High, ReasoningLevel.Default));
            try
            {
                object? plain = ToolTip.GetTip(combo);

                combo.SelectedIndex = (int)ReasoningLevel.Low;
                await PumpAsync(10);
                object? lit = ToolTip.GetTip(combo);
                Assert.AreNotEqual(plain, lit, "改过档之后,提示语要换成解释怎么取消的那一条");

                combo.SelectedIndex = (int)ReasoningLevel.High;
                await PumpAsync(10);
                Assert.AreEqual(plain, ToolTip.GetTip(combo), "选回原档就该恢复成没改过的样子");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 思考档位留在模型弹层中,页脚只显示当前模型和档位摘要。
    /// </summary>
    [TestMethod]
    public void ReasoningPicker_IsInsideTheModelPopup()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, TwoModels(ReasoningLevel.Default, ReasoningLevel.Default));
            try
            {
                Border popupBody = Find<Border>(panel, "ModelPopupBody");
                Assert.Contains(popupBody, combo.GetVisualAncestors(),
                    "思考档位在模型弹层内,不再单独占工具条宽度");
                var modelControls = (Grid)Find<Button>(panel, "ModelButton").Parent!;
                Assert.AreEqual(GridUnitType.Star,
                    modelControls.ColumnDefinitions[Grid.GetColumn(Find<Button>(panel, "ModelButton"))].Width.GridUnitType);
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 缺少宿主主题时仍能读出档位,临时修改只通过样式类标记。
    /// </summary>
    /// <remarks>
    /// 这条是真机上撞出来的:原先在代码里 <c>FindResource</c> 取色写到控件上,
    /// 而本插件允许在拿不到宿主主题令牌的环境下装载(见 <c>Panel_Loads_WithoutHostThemeTokens</c>)——
    /// 那时取回 null,前景色被设成 null,文字整个消失,下拉只剩一个箭头。
    /// </remarks>
    [TestMethod]
    public void ReasoningPicker_WithoutHostTheme_RetainsReadableTextAndOverrideClass()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, TwoModels(ReasoningLevel.Default, ReasoningLevel.Default));
            try
            {
                Assert.IsNotNull(combo.Foreground, "未加载宿主主题时仍须保留可见文字");
                Assert.DoesNotContain("overridden", combo.Classes);
                panel.Resources["VelaAccentDim"] = Brushes.CornflowerBlue;
                Button modelButton = Find<Button>(panel, "ModelButton");
                IBrush? modelBackground = modelButton.Background;

                combo.SelectedIndex = (int)ReasoningLevel.High;
                await PumpAsync(10);
                Assert.Contains("overridden", combo.Classes, "思考选择框保留临时覆盖标记");
                Assert.AreEqual(modelBackground, modelButton.Background,
                    "调整思考强度不应高亮模型入口");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>models.dev 明说不会思考的模型,档位没得调 —— 灰掉,并说明为什么。</summary>
    [TestMethod]
    public void ReasoningPicker_IsDisabledForModelsThatCannotThink()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = TwoModels(ReasoningLevel.Default, ReasoningLevel.Default);
            provider.Models[0].SupportsReasoning = false; // 拉过规格库,答案是"不会"
            provider.Models[1].SupportsReasoning = true;
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, provider);
            try
            {
                Assert.IsFalse(combo.IsEnabled);

                Find<ListBox>(panel, "ProviderList").SelectedIndex = 1;
                await PumpAsync(10);
                Assert.IsTrue(combo.IsEnabled, "换到会思考的模型就该放开");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 手工添加的模型、中转站的私有型号都没拉过规格库 —— 那时是"不知道",不是"不会"。
    /// 凭默认值去灰掉一个其实能思考的模型,比多给一个无效档位糟得多。
    /// </summary>
    [TestMethod]
    public void ReasoningPicker_StaysOpenWhenTheCapabilityIsUnknown()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = TwoModels(ReasoningLevel.Default, ReasoningLevel.Default);
            Assert.IsNull(provider.Models[0].SupportsReasoning, "没拉过规格库就该是 null");
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, provider);
            try
            {
                Assert.IsTrue(combo.IsEnabled);
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 换模型要清掉。各家的档位含义与默认值本来就不同,留着它等于把
    /// "我给上一个模型选的高"悄悄套到下一个头上。
    /// </summary>
    [TestMethod]
    public void SwitchingModels_DropsTheTemporaryLevel()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, TwoModels(ReasoningLevel.Default, ReasoningLevel.Off));
            try
            {
                combo.SelectedIndex = (int)ReasoningLevel.High;
                await PumpAsync(10);

                Find<ListBox>(panel, "ProviderList").SelectedIndex = 1;
                await PumpAsync(10);

                Assert.AreEqual((int)ReasoningLevel.Off, combo.SelectedIndex,
                    "换到第二个模型,显示的该是它自己配的那一档,不是上一个模型的临时选择");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 在设置里删掉当前模型、自动落到另一份时,旧模型那份临时档位必须一起作废 ——
    /// 这次重载被 <c>_syncingProviders</c> 拦在选择处理器之外,没人替它清;
    /// 留着下一轮会把 A 的 High 发给别的模型,哪怕 B 根本不支持思考。
    /// </summary>
    [TestMethod]
    public void DeletingTheCurrentModel_DropsItsTemporaryLevel()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = TwoModels(ReasoningLevel.Default, ReasoningLevel.Off);
            await new AiSettingsStore(context).SaveAsync(
                new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            ComboBox combo = Find<ComboBox>(panel, "ReasoningCombo");
            Window? host = null;
            try
            {
                combo.SelectedIndex = (int)ReasoningLevel.High;
                await PumpAsync(10);
                Assert.AreEqual((int)ReasoningLevel.High, combo.SelectedIndex, "前提:挂着临时档位");

                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var settings = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(settings);
                await PumpAsync(10);

                ListBox list = FindIn<ListBox>(settings, "ProvidersList");
                list.SelectedIndex = 1; // 导航表:第 0 行是供应商,1 起是它的模型 —— 当前选中的那份
                await PumpAsync(10);
                RaiseClick(FindIn<Button>(settings, "DeleteButton"));
                await PumpAsync(20);

                AiSettings reloaded = await new AiSettingsStore(context).LoadAsync();
                Assert.HasCount(1, reloaded.Providers[0].Models, "前提:当前模型真的被删了");
                Assert.AreEqual((int)ReasoningLevel.Off, combo.SelectedIndex,
                    "旧模型的临时档位不许留着 —— 显示的该是剩下那份自己配的档,下一轮也按它发");
                Assert.AreEqual(provider.Models[1].Id, reloaded.ActiveModelId,
                    "自动落到第二份上,并写回设置");
            }
            finally
            {
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void AddingTheFirstModel_DropsTheTemporaryLevelPickedWhileNoModelExisted()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = TwoModels(ReasoningLevel.Default, ReasoningLevel.Default);
            provider.Models.Clear();
            await new AiSettingsStore(context).SaveAsync(new AiSettings { Providers = [provider] });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            ComboBox tier = Find<ComboBox>(panel, "ReasoningCombo");
            Window? host = null;
            try
            {
                tier.SelectedIndex = (int)ReasoningLevel.High;
                Assert.Contains("overridden", tier.Classes, "无模型时可以临时选中高档");

                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(editor);
                await PumpAsync(10);
                FindIn<ListBox>(editor, "ProvidersList").SelectedIndex = 0;
                RaiseClick(FindIn<Button>(editor, "AddModelButton"));
                Assert.IsTrue(await WaitForAsync(() => Find<ListBox>(panel, "ProviderList").SelectedIndex == 0));
                Assert.AreEqual((int)ReasoningLevel.Default, tier.SelectedIndex,
                    "首个模型有自己的默认档，之前空选择的 High 不可继承");
                Assert.DoesNotContain("overridden", tier.Classes);
            }
            finally
            {
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void UpdatingSameModelToUnsupported_DropsTemporaryTierButKeepsRunningTurnSnapshot()
    {
        OnUi(async () =>
        {
            await using var stub = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"好的"},"finish_reason":"stop"}]}

                data: [DONE]


                """, hold: true);
            using var context = new TestPluginContext();
            AiProvider provider = TwoModels(ReasoningLevel.Default, ReasoningLevel.Default);
            provider.BaseUrl = stub.BaseUrl;
            (Window window, ChatPanelView panel, ComboBox combo) = await WithReasoningPickerAsync(context, provider);
            Window? host = null;
            try
            {
                combo.SelectedIndex = (int)ReasoningLevel.High;
                panel.SendExternal("第一轮");
                Assert.IsTrue(await WaitForAsync(() => stub.Requests.Count == 1), "第一轮真发出去了");
                Assert.Contains("reasoning_effort", stub.Requests[0], "开跑时的临时档位已经随请求送出");

                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var settings = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(settings);
                await PumpAsync(10);
                ListBox list = FindIn<ListBox>(settings, "ProvidersList");
                list.SelectedIndex = 1;
                await PumpAsync(10);
                ((ProviderNavItem)list.SelectedItem!).Model!.SupportsReasoning = false;
                RaiseClick(FindIn<Button>(settings, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => !combo.IsEnabled), "同一模型新规格明确不支持思考,下拉必须变灰");
                Assert.DoesNotContain("overridden", combo.Classes, "旧临时档位也必须作废,不能留一个隐藏的 High");
                Assert.AreEqual((int)ReasoningLevel.Default, combo.SelectedIndex);

                stub.Release();
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1), "正在执行的轮次照原快照答完");
                panel.SendExternal("第二轮");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 2 && stub.Requests.Count >= 2));
                Assert.DoesNotContain("reasoning_effort", stub.Requests[1],
                    "下一轮按同模型的新规格发,不许夹带旧临时档位");
            }
            finally
            {
                stub.Release();
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }
    [TestMethod]
    public void SavingTemporaryHighAsModelDefault_ClearsOverrideAndLaterSettingsStayVisible()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = TwoModels(ReasoningLevel.Default, ReasoningLevel.Default);
            (Window window, ChatPanelView panel, ComboBox tier) = await WithReasoningPickerAsync(context, provider);
            Window? host = null;
            try
            {
                tier.SelectedIndex = (int)ReasoningLevel.High;
                Assert.Contains("overridden", tier.Classes);
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(editor);
                await PumpAsync(10);
                FindIn<ListBox>(editor, "ProvidersList").SelectedIndex = 1;
                await PumpAsync(10);
                ComboBox savedTier = FindIn<ComboBox>(editor, "ReasoningCombo");
                savedTier.SelectedIndex = (int)ReasoningLevel.High;
                RaiseClick(FindIn<Button>(editor, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(editor, "StatusText").Text == "Saved."
                    && !tier.Classes.Contains("overridden")), "保存成与临时档相同的 High 后清掉无意义覆盖");
                savedTier.SelectedIndex = (int)ReasoningLevel.Low;
                RaiseClick(FindIn<Button>(editor, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => tier.SelectedIndex == (int)ReasoningLevel.Low
                    && !tier.Classes.Contains("overridden")), "后续持久档位变化不能被旧 High 遮盖");
                Assert.AreEqual(ReasoningLevel.Low,
                    (await new AiSettingsStore(context).LoadAsync()).Providers[0].Models[0].Reasoning);
            }
            finally
            {
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void FirstStation_CapabilityDropsDuringMcpHandshake_RejectsOldTurnUntilNewUserRequest()
    {
        OnUi(async () =>
        {
            await using var mcp = new SseStub("", hold: true);
            await using var target = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m1","choices":[{"index":0,"delta":{"content":"已回答"},"finish_reason":"stop"}]}

                data: [DONE]

                """);
            using var context = new TestPluginContext();
            AiProvider provider = TwoModels(ReasoningLevel.Default, ReasoningLevel.Default);
            provider.BaseUrl = target.BaseUrl;
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                McpServers = [new McpServerConfig { Name = "held", Transport = McpTransportType.Http,
                    Url = mcp.BaseUrl + "/mcp" }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                Find<ComboBox>(panel, "ReasoningCombo").SelectedIndex = (int)ReasoningLevel.High;
                Find<ComboBox>(panel, "ModeCombo").SelectedIndex = (int)ChatMode.Agent;
                panel.SendExternal("首站必须放弃旧 High");
                Assert.IsTrue(await WaitForAsync(() => mcp.RequestBodyAsync.IsCompleted), "MCP 网络握手仍在途,首站尚未发流");
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(editor);
                await PumpAsync(10);
                ListBox list = FindIn<ListBox>(editor, "ProvidersList");
                list.SelectedIndex = 1;
                await PumpAsync(10);
                ((ProviderNavItem)list.SelectedItem!).Model!.SupportsReasoning = false;
                RaiseClick(FindIn<Button>(editor, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(editor, "StatusText").Text == "Saved."));
                mcp.Release();
                Assert.IsTrue(await WaitForAsync(() => Footers(Find<StackPanel>(panel, "MessagesPanel")).Count == 1));
                Assert.IsEmpty(target.Requests, "请求配置已保存变化,旧轮不能重新授权后发送");
                panel.SendExternal("按新能力重新发起");
                Assert.IsTrue(await WaitForAsync(() => !Find<Viewbox>(panel, "StopIcon").IsVisible && target.Requests.Count == 1));
                Assert.Contains("\"stream\":true", target.Requests[0]);
                Assert.DoesNotContain("reasoning_effort", target.Requests[0], "显式新轮按当前能力装配");
            }
            finally
            {
                mcp.Release();
                host?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }
}

/// <summary>临时档位在模型层的语义(与界面无关,单独测)。</summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class ReasoningOverrideTests
{
    private static ResolvedModel Model(ReasoningLevel configured)
    {
        var config = new AiModelConfig { Model = "m", Reasoning = configured };
        return new ResolvedModel(
            new AiProvider { Name = "p", BaseUrl = "http://127.0.0.1:1/v1", Models = [config] }, config);
    }

    [TestMethod]
    public void WithReasoning_LeavesTheOriginalAlone()
    {
        ResolvedModel original = Model(ReasoningLevel.Low);

        ResolvedModel raised = original.WithReasoning(ReasoningLevel.High);

        Assert.AreEqual(ReasoningLevel.High, raised.Reasoning);
        // 界面会缓存并共享 ResolvedModel —— 就地改的话,某一轮的临时选择会渗到别处去
        Assert.AreEqual(ReasoningLevel.Low, original.Reasoning, "原对象不能被改动");
        Assert.AreEqual(ReasoningLevel.Low, original.Config.Reasoning, "更不该动到底下的配置");
    }

    [TestMethod]
    public void WithReasoning_Null_MeansUseWhateverTheModelSays()
    {
        ResolvedModel original = Model(ReasoningLevel.Medium);

        Assert.AreSame(original, original.WithReasoning(null));
        Assert.AreEqual(ReasoningLevel.Medium, original.WithReasoning(null).Reasoning);
    }

    /// <summary>临时档位得真的走到请求里去,而不是只改了个界面显示。</summary>
    [TestMethod]
    public void TheTemporaryLevel_ReachesTheRequest()
    {
        var options = new Microsoft.Extensions.AI.ChatOptions();

        AiSettingsStore.ApplyReasoning(options, Model(ReasoningLevel.Default).WithReasoning(ReasoningLevel.High));

        Assert.IsNotNull(options.Reasoning);
        Assert.AreEqual(Microsoft.Extensions.AI.ReasoningEffort.High, options.Reasoning.Effort);
    }

    [TestMethod]
    public void WithoutAnOverride_DefaultStillMeansSendNothing()
    {
        var options = new Microsoft.Extensions.AI.ChatOptions();

        AiSettingsStore.ApplyReasoning(options, Model(ReasoningLevel.Default));

        Assert.IsNull(options.Reasoning, "Default 的含义就是请求里不带 reasoning 参数");
    }

    /// <summary>
    /// models.dev 的 <c>reasoning</c> 要落到模型配置上,而且 <c>false</c> 必须能写进去 ——
    /// 其余几项走的是"0 就当没说过",照搬那条规矩的话永远回不到 false。
    /// </summary>
    [TestMethod]
    public void PullingSpecs_RecordsWhetherTheModelCanThink()
    {
        var model = new AiModelConfig { Model = "m" };

        ModelsDevCatalog.Apply(model, new ModelSpec("m", "M", 128000, 8192, 1, 2, 0, true), newModel: true);
        Assert.IsTrue(model.SupportsReasoning);

        ModelsDevCatalog.Apply(model, new ModelSpec("m", "M", 128000, 8192, 1, 2, 0, false));
        Assert.IsFalse(model.SupportsReasoning, "拉到「不会思考」就得真的写下来");
    }

    [TestMethod]
    public void ReasoningAdjustable_OnlyClosesWhenWeActuallyKnow()
    {
        var unknown = new AiModelConfig { Model = "m" };
        var cannot = new AiModelConfig { Model = "m", SupportsReasoning = false };
        var can = new AiModelConfig { Model = "m", SupportsReasoning = true };
        AiProvider p = new() { Name = "p", BaseUrl = "http://127.0.0.1:1/v1", Models = [unknown, cannot, can] };

        Assert.IsTrue(new ResolvedModel(p, unknown).ReasoningAdjustable, "不知道就放开");
        Assert.IsFalse(new ResolvedModel(p, cannot).ReasoningAdjustable);
        Assert.IsTrue(new ResolvedModel(p, can).ReasoningAdjustable);
    }
}
