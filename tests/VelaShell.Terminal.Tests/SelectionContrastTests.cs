using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 选区高亮看不看得见(#选区对比度)。
/// <para>
/// 回归的是这么一个具体故障:选区色此前按半透明叠加绘制(暗 35% / 亮 25%),而各家方案的
/// 选区色本就是照不透明填充设计的、与自家背景只差一线,乘上不透明度后实测对比度只剩
/// 1.05:1 ~ 1.21:1 —— 拖完一段跟没拖一样,用户以为压根没选中。
/// </para>
/// </summary>
[TestClass]
[TestCategory("TerminalPalette")]
public class SelectionContrastTests
{
    private static Avalonia.Headless.HeadlessUnitTestSession Session => HeadlessTestSession.Current;

    private static void OnUi(Action body) =>
        Session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>各家方案的背景 + 原生选区色。前四组是原本淡到看不见的那几套。</summary>
    private static IEnumerable<object[]> Schemes =>
    [
        ["Solarized Light", Hex(0xFDF6E3), Hex(0xEEE8D5)],
        ["Solarized Dark", Hex(0x002B36), Hex(0x073642)],
        ["One Light", Hex(0xFAFAFA), Hex(0xE5E5E6)],
        ["GitHub Light", Hex(0xFFFFFF), Hex(0xCFE4FB)],
        ["Dracula", Hex(0x282A36), Hex(0x44475A)],
        ["Tokyo Night", Hex(0x1A1B26), Hex(0x33467C)],
        ["Nord", Hex(0x2E3440), Hex(0x434C5E)],
        ["Alucard", Hex(0xFFFBEB), Hex(0xCFCFDE)],
        ["Monokai", Hex(0x272822), Hex(0x49483E)],
        ["Sakura", Hex(0xFFFBFD), Hex(0xF7D9E5)],
        ["Obsidian", Hex(0x131316), Hex(0x2E2E38)],
        ["Rosé Pine Dawn", Hex(0xFFFAF3), Hex(0xEADDD3)],
    ];

    [TestMethod]
    [DynamicData(nameof(Schemes))]
    public void Fill_AlwaysClearsTheVisibilityFloor(string name, Rgba background, Rgba selection)
    {
        Rgba fill = SelectionContrast.Fill(selection, background);

        double delta = Math.Abs(SelectionContrast.Lightness(fill) - SelectionContrast.Lightness(background));
        Assert.IsGreaterThanOrEqualTo(SelectionContrast.MinLightnessDeltaFor(background) - 0.01, delta,
            $"{name}:选区底与终端底只差 L* {delta:F1},低于 {SelectionContrast.MinLightnessDeltaFor(background)} 就等于没画。");
        Assert.AreEqual(0xFF, fill.A, $"{name}:选区底必须不透明 —— 半透明正是原先看不见的根因。");
    }

    /// <summary>够用的方案不该被动:整定只托底,不夺方案自己的设计。</summary>
    [TestMethod]
    public void Fill_LeavesAlreadyVisibleSchemesAlone() =>
        // Tokyo Night 原生 ΔL* 20.6,是内置 16 套里唯一自己就跨过暗色档(20)的。
        Assert.AreEqual(Hex(0x33467C), SelectionContrast.Fill(Hex(0x33467C), Hex(0x1A1B26)), "Tokyo Night");

    /// <summary>
    /// 阈值分两档:暗底 20、亮底 16(对齐 VS Code 自家明暗主题的 20.8 / 15.9)。
    /// <para>
    /// 此前明暗共用 14。Monokai 的原生 ΔL* 14.6 曾因此原样放行,归到暗色档下就得被推开 ——
    /// 这条用例钉的正是这次抬档:换回单一 14 会红。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Fill_UsesASeparateFloorPerBackgroundLightness()
    {
        Assert.AreEqual(20.0, SelectionContrast.MinLightnessDeltaFor(Hex(0x272822)), 1e-9, "暗底走 20 那一档。");
        Assert.AreEqual(16.0, SelectionContrast.MinLightnessDeltaFor(Hex(0xFDF6E3)), 1e-9, "亮底走 16 那一档。");

        Assert.AreNotEqual(Hex(0x49483E), SelectionContrast.Fill(Hex(0x49483E), Hex(0x272822)),
            "Monokai 原生只有 ΔL* 14.6,跨不过暗色档,不该被原样放行。");

        // 亮底吃的是 16 那一档,不该被暗色档带高 —— 压得越深越贴近亮色主题的深色文字。
        Rgba onLight = SelectionContrast.Fill(Hex(0xEEE8D5), Hex(0xFDF6E3));
        double delta = Math.Abs(
            SelectionContrast.Lightness(onLight) - SelectionContrast.Lightness(Hex(0xFDF6E3)));
        Assert.IsTrue(delta is >= 16.0 - 0.01 and < 20.0, $"亮底整定到 L* {delta:F1},应落在 16 那一档。");
    }

    /// <summary>推的方向由背景明暗定:暗底上提亮、亮底上压深。</summary>
    [TestMethod]
    public void Fill_PushesAwayFromTheBackground()
    {
        Rgba onDark = SelectionContrast.Fill(Hex(0x073642), Hex(0x002B36));
        Rgba onLight = SelectionContrast.Fill(Hex(0xEEE8D5), Hex(0xFDF6E3));

        Assert.IsGreaterThan(SelectionContrast.Lightness(Hex(0x002B36)), SelectionContrast.Lightness(onDark));
        Assert.IsLessThan(SelectionContrast.Lightness(Hex(0xFDF6E3)), SelectionContrast.Lightness(onLight));
    }

    /// <summary>选区色与背景撞成同一个色也要能推开(最坏情况不能死循环或原样返回)。</summary>
    [TestMethod]
    public void Fill_HandlesSelectionIdenticalToBackground()
    {
        foreach (Rgba bg in new[] { Hex(0x000000), Hex(0xFFFFFF), Hex(0x808080), Hex(0x282A36) })
        {
            Rgba fill = SelectionContrast.Fill(bg, bg);
            double delta = Math.Abs(SelectionContrast.Lightness(fill) - SelectionContrast.Lightness(bg));
            Assert.IsGreaterThanOrEqualTo(SelectionContrast.MinLightnessDeltaFor(bg) - 0.01, delta, $"背景 {bg.Packed:X8} 上推不开。");
        }
    }

    /// <summary>不撞(或撞了但对比度本来就够)的一律原样,免得选中一段就把 ls --color 的配色抹平。</summary>
    [TestMethod]
    public void ReadableOnSelection_LeavesWhatIsAlreadyReadableAlone()
    {
        Rgba fill = SelectionContrast.Fill(Hex(0x073642), Hex(0x002B36));

        Assert.AreEqual(Hex(0xFFFFFF), SelectionContrast.ReadableOnSelection(Hex(0xFFFFFF), fill));
        Assert.AreEqual(Hex(0x000000), SelectionContrast.ReadableOnSelection(Hex(0x000000), Hex(0xFFFFFF)));
    }

    /// <summary>
    /// 撞上的前景<b>保色相、只推明度</b>:推完对比度够 3:1,色相与原色一致。
    /// <para>
    /// 此前撞上的格一律换成同一个兜底色(默认前景,它也撞就纯白 / 纯黑)—— 这条用例在那时会红:
    /// 兜底色是灰的,色相对不上。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ReadableOnSelection_KeepsTheHue_AndReachesTheContrastFloor()
    {
        // Solarized Dark 的 ansi blue / magenta / cyan / violet / black 落在推亮之后的选区底上,正是会撞的那几个。
        Rgba fill = SelectionContrast.Fill(Hex(0x073642), Hex(0x002B36));
        foreach (Rgba color in new[] { Hex(0x268BD2), Hex(0xD33682), Hex(0x2AA198), Hex(0x6C71C4), Hex(0x073642) })
        {
            Rgba fixedColor = SelectionContrast.ReadableOnSelection(color, fill);

            Assert.IsGreaterThanOrEqualTo(SelectionContrast.MinForegroundContrast, Contrast(fixedColor, fill),
                $"{color.Packed:X8} → {fixedColor.Packed:X8} 推完仍不够。");
            AssertSameHue(color, fixedColor);
        }
    }

    /// <summary>往前景本来所在的那一侧推(改动最小),那一侧推到头也够不着才换边。</summary>
    [TestMethod]
    public void ReadableOnSelection_PushesTowardTheSideItIsAlreadyOn()
    {
        var grey = Rgba.FromRgb(0x80, 0x80, 0x80);
        var slightlyBrighter = Rgba.FromRgb(0x90, 0x90, 0x90);
        var slightlyDarker = Rgba.FromRgb(0x70, 0x70, 0x70);

        Assert.IsGreaterThan(SelectionContrast.Brightness(slightlyBrighter),
            SelectionContrast.Brightness(SelectionContrast.ReadableOnSelection(slightlyBrighter, grey)));
        Assert.IsLessThan(SelectionContrast.Brightness(slightlyDarker),
            SelectionContrast.Brightness(SelectionContrast.ReadableOnSelection(slightlyDarker, grey)));

        // 选区底贴着黑:往暗里推到纯黑也只有 1.2:1,只能提亮。
        var nearlyBlack = Rgba.FromRgb(0x10, 0x10, 0x10);
        var darkFill = Rgba.FromRgb(0x18, 0x18, 0x18);
        Assert.IsGreaterThan(SelectionContrast.Brightness(darkFill),
            SelectionContrast.Brightness(SelectionContrast.ReadableOnSelection(nearlyBlack, darkFill)));
    }

    /// <summary>
    /// 尺子:全部内置方案、默认前景 + ANSI 16 色压在各自整定后的选区底上 —— 渲染层判为「撞了」的那些,
    /// 推完对比度必须够 3:1(与 ANSI 压在自家终端底上的要求同一档),并且色相不变。
    /// 新增一套方案时它必须仍是绿的。
    /// </summary>
    [TestMethod]
    public void EveryBuiltInScheme_CollidingAnsiColorsBecomeReadableAndKeepTheirHue()
    {
        var failures = new List<string>();
        int collided = 0;
        foreach (Core.Models.TerminalColorScheme scheme in VelaShell.Core.Models.TerminalColorScheme.BuiltIn)
        {
            Rgba fill = SelectionContrast.Fill(Parse(scheme.Selection), Parse(scheme.Background));
            double fillBrightness = SelectionContrast.Brightness(fill);
            foreach (string literal in scheme.AnsiNormal.Concat(scheme.AnsiBright).Prepend(scheme.Foreground))
            {
                Rgba color = Parse(literal);
                if (Math.Abs(SelectionContrast.Brightness(color) - fillBrightness) >= SelectionContrast.MinForegroundDelta)
                {
                    continue; // 渲染层不动它。
                }
                collided++;
                Rgba drawn = SelectionContrast.ReadableOnSelection(color, fill);
                if (Contrast(drawn, fill) < SelectionContrast.MinForegroundContrast)
                {
                    failures.Add($"{scheme.Name}: {literal} → {drawn.Packed & 0xFFFFFF:X6} 压在选区底上只有 {Contrast(drawn, fill):F2}:1");
                }
                if (HueShift(color, drawn) is var shift && shift > HueTolerance(drawn))
                {
                    failures.Add($"{scheme.Name}: {literal} → {drawn.Packed & 0xFFFFFF:X6} 色相偏了 {shift:F1}°");
                }
            }
        }
        Assert.IsGreaterThan(0, collided, "一个撞色的都没有,尺子就没量到东西。");
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    private static double Contrast(Rgba a, Rgba b) =>
        SelectionContrast.ContrastRatio(SelectionContrast.RelativeLuminance(a), SelectionContrast.RelativeLuminance(b));

    private static void AssertSameHue(Rgba expected, Rgba actual) =>
        Assert.IsLessThanOrEqualTo(HueTolerance(actual), HueShift(expected, actual),
            $"{expected.Packed:X8} → {actual.Packed:X8}:色相应当不变。");

    /// <summary>
    /// 色相允许的偏差:3°,外加 8 位取整带来的那一份 —— 推到很暗的颜色三个通道只剩十几个色阶,
    /// 一个色阶的取整误差就能让色相挪 <c>60° / 色度</c>(Nord 的 #4C566A 压到 #13161A,色度只有 7)。
    /// </summary>
    private static double HueTolerance(Rgba color)
    {
        int chroma = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
        return chroma == 0 ? 3.0 : Math.Max(3.0, 60.0 / chroma);
    }

    /// <summary>两色在 HSV 色相上差几度;任一方接近灰色(饱和度太低,色相无意义)时记 0。</summary>
    private static double HueShift(Rgba a, Rgba b)
    {
        (double hueA, double satA) = HueAndSaturation(a);
        (double hueB, double satB) = HueAndSaturation(b);
        if (satA < 0.08 || satB < 0.08)
        {
            return 0;
        }
        double d = Math.Abs(hueA - hueB) % 360;
        return d > 180 ? 360 - d : d;
    }

    private static (double Hue, double Saturation) HueAndSaturation(Rgba color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double chroma = max - min;
        if (chroma <= 0)
        {
            return (0, 0);
        }
        double hue = max == r ? 60 * (((g - b) / chroma) % 6)
            : max == g ? 60 * (((b - r) / chroma) + 2)
            : 60 * (((r - g) / chroma) + 4);
        return (hue < 0 ? hue + 360 : hue, chroma / max);
    }

    private static Rgba Parse(string literal) =>
        Hex(uint.Parse(literal.TrimStart('#'), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>控件层:三层配色叠完后落到调色板上的,是整定过的填充色。</summary>
    [TestMethod]
    public void Control_NormalizesSelectionAfterAllPaletteLayers()
    {
        OnUi(() =>
        {
            // Solarized Light 那套:选区 base2 与背景 base3 原生只差 L* 5,是最淡的一档。
            var control = new VelaTerminalControl
            {
                ThemePalette = new()
                {
                    Background = Hex(0xFDF6E3),
                    Foreground = Hex(0x657B83),
                    Selection = Hex(0xEEE8D5),
                },
            };

            TerminalPalette palette = control.PaletteForTest;
            double delta = Math.Abs(
                SelectionContrast.Lightness(palette.SelectionBackground)
                - SelectionContrast.Lightness(palette.DefaultBackground));

            Assert.AreEqual(0xFF, palette.SelectionBackground.A);
            Assert.IsGreaterThanOrEqualTo(SelectionContrast.MinLightnessDeltaFor(palette.DefaultBackground) - 0.01, delta,
                $"只差 L* {delta:F1}。");
        });
    }

    private static Rgba Hex(uint rgb) => Rgba.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
