using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Controls;
using VelaShell.Controls.Controls;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.ViewModels;
using VelaShell.Views;
using VelaShell.Views.Settings;

namespace VelaShell.Tests.Views;

/// <summary>
/// 颜色字段(<see cref="ColorPickerField" />):字段回显、浮层起始状态、各条写回路径,
/// 以及设置页与连接对话框确实用上了它。
/// </summary>
[TestClass]
[TestCategory("ColorPickerUi")]
public sealed class ColorPickerFieldUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ColorPickerFieldUiTests).Assembly);

    [TestMethod]
    public void Field_ShowsTheValue_OrTheEmptyTextWhenThereIsNoColor()
    {
        OnField(() => new ColorPickerField { EmptyText = "自动配色" }, (field, _) =>
        {
            TextBlock text = Part<TextBlock>(field, "ValueText");
            Border chip = Part<Border>(field, "Chip");
            Assert.AreEqual("自动配色", text.Text);
            Assert.IsTrue(text.Classes.Contains("empty"), "没有颜色时那行字是说明,要用弱一档的样式。");
            Assert.IsNull(chip.Background, "没有颜色时色块是一个空框。");

            field.Value = "#e05252";
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("#E05252", text.Text);
            Assert.IsFalse(text.Classes.Contains("empty"));
            Assert.AreEqual(Color.Parse("#E05252"), ((ISolidColorBrush)chip.Background!).Color);

            // 认不出的色值(手改过的配置)当作没有颜色显示,而不是把乱码当色值摆出来。
            field.Value = "not-a-color";
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("自动配色", text.Text);
        });
    }

    [TestMethod]
    public void OpeningThePicker_StartsFromTheCurrentValue()
    {
        OnField(() => new ColorPickerField { Value = "#3366CC" }, (field, _) =>
        {
            field.Open();
            Dispatcher.UIThread.RunJobs();

            HsvColor expected = Color.Parse("#3366CC").ToHsv();
            ColorSpectrumPad pad = Part<ColorSpectrumPad>(field, "Pad");
            Assert.AreEqual(expected.H, pad.Hue, 0.5);
            Assert.AreEqual(expected.S, pad.Saturation, 0.01);
            Assert.AreEqual(expected.V, pad.Value, 0.01);
            Assert.AreEqual(expected.H, Part<HueStrip>(field, "HueBar").Hue, 0.5);
            Assert.AreEqual("#3366CC", Part<TextBox>(field, "HexBox").Text);
            Assert.AreEqual(Color.Parse("#3366CC"),
                ((ISolidColorBrush)Part<Border>(field, "OriginalChip").Background!).Color,
                "新旧对比的左半是打开浮层时的颜色。");
        });
    }

    [TestMethod]
    public void ClickingASwatch_WritesItBackAndMarksIt()
    {
        OnField(() => new ColorPickerField { Value = "#112233", Swatches = ["#112233", "#445566"], SwatchesTitle = "方案" }, (field, _) =>
        {
            field.Open();
            Dispatcher.UIThread.RunJobs();

            Button[] swatches = [.. Part<WrapPanel>(field, "SwatchPanel").Children.OfType<Button>()];
            Assert.HasCount(2, swatches);
            Assert.IsTrue(swatches[0].Classes.Contains("current"), "当前颜色在色板里的那一格要标出来。");
            Assert.AreEqual("方案", Part<TextBlock>(field, "SwatchTitle").Text);

            swatches[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.AreEqual("#445566", field.Value);
            Assert.IsTrue(swatches[1].Classes.Contains("current"));
            Assert.IsFalse(swatches[0].Classes.Contains("current"));
        });
    }

    [TestMethod]
    public void WithoutSwatches_TheThemePaletteIsOffered()
    {
        OnField(() => new ColorPickerField(), (field, _) =>
        {
            field.Open();
            Dispatcher.UIThread.RunJobs();

            Assert.HasCount(8, Part<WrapPanel>(field, "SwatchPanel").Children,
                "没给色板时用当前主题的强调色板(VelaAccentPalette0..7)。");
            Assert.IsTrue(Part<StackPanel>(field, "SwatchSection").IsVisible);
        });
    }

    [TestMethod]
    public void HexBox_CommitsAValidColor_AndRevertsAnInvalidOne()
    {
        OnField(() => new ColorPickerField { Value = "#000000" }, (field, _) =>
        {
            field.Open();
            Dispatcher.UIThread.RunJobs();
            TextBox hex = Part<TextBox>(field, "HexBox");

            hex.Text = "#00ff88";
            PressEnter(hex);
            Assert.AreEqual("#00FF88", field.Value, "写出去一律是大写六位。");

            // 敲错的色值不能写进设置:终端四色认不出会整套落回出厂色。框里的字退回当前颜色。
            hex.Text = "nope";
            PressEnter(hex);
            Assert.AreEqual("#00FF88", field.Value);
            Assert.AreEqual("#00FF88", hex.Text);
        });
    }

    [TestMethod]
    public void AdjustingThePadWithTheKeyboard_WritesBack()
    {
        OnField(() => new ColorPickerField { Value = "#3366CC" }, (field, _) =>
        {
            field.Open();
            Dispatcher.UIThread.RunJobs();

            Part<ColorSpectrumPad>(field, "Pad").RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Down,
                KeyModifiers = KeyModifiers.Shift
            });
            Dispatcher.UIThread.RunJobs();

            Assert.AreNotEqual("#3366CC", field.Value);
            Assert.IsTrue(ColorHex.TryParse(field.Value, out Color darker));
            Assert.IsLessThan(Color.Parse("#3366CC").ToHsv().V, darker.ToHsv().V, "Shift+↓ 把明度压低一档。");
        });
    }

    /// <summary>
    /// 拖动过程中不写回:强调色一改就要重派生全套主题令牌,终端颜色一改就要重刷所有终端,
    /// 跟着鼠标每像素提交一次是白费。只有松手那一下算数。
    /// </summary>
    [TestMethod]
    public void DraggingThePad_CommitsOnlyOnRelease()
    {
        _session.Dispatch(() =>
        {
            var pad = new ColorSpectrumPad
            {
                Width = 200,
                Height = 100,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            };
            int commits = 0;
            pad.Committed += (_, _) => commits++;
            var window = new Window { Content = pad, Width = 300, Height = 200 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                window.MouseDown(new Point(50, 20), MouseButton.Left);
                window.MouseMove(new Point(150, 80));
                Dispatcher.UIThread.RunJobs();
                Assert.AreEqual(0, commits, "还按着的时候不提交。");
                Assert.AreEqual(0.75, pad.Saturation, 0.01);
                Assert.AreEqual(0.2, pad.Value, 0.01);

                window.MouseUp(new Point(150, 80), MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.AreEqual(1, commits, "松手提交一次。");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ClearButton_AppearsOnlyWhereEmptyIsAllowed_AndEmptiesTheValue()
    {
        OnField(() => new ColorPickerField { Value = "#123456" }, (field, _) =>
        {
            Button clear = Part<Button>(field, "ClearButton");
            Assert.IsFalse(clear.IsVisible, "终端四色必填:没给 ClearText 就没有这一键。");

            field.ClearText = "自动配色";
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(clear.IsVisible);
            Assert.AreEqual("自动配色", clear.Content);

            field.Open();
            Dispatcher.UIThread.RunJobs();
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(string.Empty, field.Value);
        });
    }

    /// <summary>连接对话框「终端」页的标签颜色:是取色器,不再是手敲十六进制的输入框;值双向绑到视图模型。</summary>
    [TestMethod]
    public void ConnectionDialog_TabColorIsPicked()
    {
        _session.Dispatch(() =>
        {
            var vm = new ConnectionProfileViewModel
            {
                OverrideTabColor = "#E05252",
                SelectedSection = ConnectionProfileSection.Terminal
            };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                ColorPickerField field = window.GetVisualDescendants().OfType<ColorPickerField>().Single();
                Assert.AreEqual("#E05252", field.Value);
                Assert.DoesNotContain(box => box.Text == "#E05252", window.GetVisualDescendants().OfType<TextBox>(),
                    "标签颜色不该再有手敲的输入框。");

                field.Value = "#50FA7B";
                Assert.AreEqual("#50FA7B", vm.OverrideTabColor);

                // 清回自动配色:空串存回 null,圆点也跟着灭。
                field.Value = string.Empty;
                Assert.IsFalse(vm.IsTerminalSectionModified);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>设置 → 外观:强调色与终端四色都换成了取色器,终端四色的色板是当前方案的 16 色。</summary>
    [TestMethod]
    public void AppearancePage_UsesThePickerForAccentAndTerminalColors()
    {
        _session.Dispatch(() =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            IThemeService theme = Substitute.For<IThemeService>();
            settings.GetSettingsAsync().Returns(new AppSettings());
            var viewModel = new SettingsViewModel(settings, theme);
            var window = new Window { Content = new AppearanceSettingsPage { DataContext = viewModel }, Width = 900, Height = 1600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                ColorPickerField[] fields = [.. window.GetVisualDescendants().OfType<ColorPickerField>()];
                Assert.HasCount(5, fields, "强调色 + 前景 / 背景 / 光标 / 选区。");
                Assert.AreEqual(viewModel.Appearance.TerminalForeground, fields[1].Value);
                Assert.HasCount(16, fields[1].Swatches!);
                Assert.AreSequenceEqual(
                    viewModel.Appearance.AnsiNormal.Concat(viewModel.Appearance.AnsiBright).ToList(), [.. fields[1].Swatches!]);

                fields[2].Value = "#101010";
                Assert.AreEqual("#101010", viewModel.Appearance.TerminalBackground);

                fields[0].Value = "#3498DB";
                Assert.AreEqual("#3498DB", viewModel.AccentColor);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void TerminalSchemeSwatches_FollowThePalette()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        var viewModel = new SettingsViewModel(settings, Substitute.For<IThemeService>());
        int raised = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.TerminalSchemeSwatches))
            {
                raised++;
            }
        };

        viewModel.Appearance.AnsiNormal = ["#000000", "#111111", "#222222", "#333333", "#444444", "#555555", "#666666", "#777777"];

        Assert.AreEqual(1, raised, "换了调色板,取色器的色板要跟着换。");
        Assert.AreEqual("#111111", viewModel.TerminalSchemeSwatches[1]);
    }

    /// <summary>把字段放进一个窗口里显示出来,跑完用例再关掉。</summary>
    private static void OnField(Func<ColorPickerField> create, Action<ColorPickerField, Window> body) =>
        _session.Dispatch(() =>
        {
            ColorPickerField field = create();
            var window = new Window { Content = new StackPanel { Children = { field } }, Width = 400, Height = 500 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                body(field, window);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// 按名字找字段里的零件:字段本身在可视树里,浮层的内容不在(它挂在弹出层上),
    /// 但它们从加载那一刻就在逻辑树里。
    /// </summary>
    private static T Part<T>(ColorPickerField field, string name) where T : Control
    {
        Button button = field.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FieldButton");
        IEnumerable<Control> candidates = field.GetVisualDescendants().OfType<Control>()
            .Concat(((Control)((Flyout)button.Flyout!).Content!).GetSelfAndLogicalDescendants().OfType<Control>());
        return candidates.OfType<T>().First(control => control.Name == name);
    }

    private static void PressEnter(TextBox box)
    {
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();
    }
}
