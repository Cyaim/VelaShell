using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Rendering;

/// <summary>
/// 选区高亮的对比度整定:把「配色方案给的选区色」换算成**真正看得见**的填充色。
/// <para>
/// 起因是选区色此前按半透明叠加绘制(暗色 35%、亮色 25%)。可各家方案(Dracula #44475A、
/// Solarized base2 #EEE8D5、GitHub Light #CFE4FB……)的选区色本来就是照「不透明填充」设计的
/// —— 它们与自家背景的差本就只有一线,再乘上 0.25~0.35 的不透明度,实测对比度只剩
/// 1.05:1 ~ 1.21:1,肉眼等同于没画。用户拖完一段以为压根没选中,正是这么来的。
/// </para>
/// <para>
/// 所以这里做两件事:选区底<b>不透明</b>绘制;并且保证它与终端底之间至少拉开
/// <see cref="MinLightnessDeltaOnDark" /> / <see cref="MinLightnessDeltaOnLight" /> 的感知明度差
/// —— 不够就把它往远离背景的方向推(暗底推向白、亮底推向黑),够了就原样用方案自己的色,
/// 不夺方案的设计。
/// </para>
/// <para>
/// 判据用 CIE L*(感知明度)而非 WCAG 对比度:后者在暗端被压缩得厉害,同一个阈值在暗色主题上
/// 只挪一点点、在亮色主题上却会把选区压成一条灰带,明暗两套没法用同一个数说话。
/// (顺带:WCAG 那把尺子在这里连量程都不够 —— 21:1 是纯黑配纯白的极限,
/// 要选区底与 Dracula 的 #282A36 之间做到 20:1,那条带子只能是近乎纯白的实心块。)
/// </para>
/// </summary>
internal static class SelectionContrast
{
    /// <summary>
    /// <b>暗色</b>终端底上,选区底必须拉开的感知明度差(CIE L*,0–100 标度)。
    /// </summary>
    /// <remarks>
    /// 对齐 VS Code 自家暗色主题的 20.8。此前取 14 是「稍低一档」的保守值,实测下来仍偏弱:
    /// 14 那一档只有 Tokyo Night(20.6)本来就够,其余方案被推到刚好 14 出头就停手,
    /// 拖选时仍要盯一眼才确认选中了。
    /// </remarks>
    internal const double MinLightnessDeltaOnDark = 20.0;

    /// <summary>
    /// <b>亮色</b>终端底上,选区底必须拉开的感知明度差(CIE L*,0–100 标度)。
    /// </summary>
    /// <remarks>
    /// <para>对齐 VS Code 自家亮色主题的 15.9。</para>
    /// <para>
    /// 比暗色低一档不是凑数:亮色主题的文字是<b>深色</b>的,而这里推选区底的方向是**压深** ——
    /// 推得越狠,选区底就越贴近文字自身的明度,<see cref="ReadableOnSelection" /> 那道守卫
    /// 被触发得越多 —— 它虽然保色相,终究要把撞色的前景提亮 / 压暗,离方案的原色越来越远。
    /// 暗色主题上推的方向是提亮,离深色文字越推越远,没有这一层张力,因此吃得起更高的一档。
    /// </para>
    /// </remarks>
    internal const double MinLightnessDeltaOnLight = 16.0;

    /// <summary>
    /// 给定终端底该用哪一档明度差 —— 由**背景**明暗决定,与选区色自身无关
    /// (推的方向同样由它决定,两者必须用同一个判据,否则会出现"往亮里推、却按暗色档收手")。
    /// </summary>
    /// <param name="background">终端默认背景色。</param>
    /// <returns><see cref="MinLightnessDeltaOnDark" /> 或 <see cref="MinLightnessDeltaOnLight" />。</returns>
    internal static double MinLightnessDeltaFor(Rgba background) =>
        Lightness(background) < 50 ? MinLightnessDeltaOnDark : MinLightnessDeltaOnLight;

    /// <summary>
    /// 选中格的前景与选区底之间的最小感知亮度差(0–1 标度)—— 「撞了」的判据,渲染热路径上逐格比的就是它。
    /// 低于此值就由 <see cref="ReadableOnSelection" /> 保色相地推开明度 —— 选区底一旦不透明,
    /// 原本压在终端底上读得清的暗色前景(Solarized Dark 的 ansi black #073642 是最极端的一例)
    /// 就可能贴到明度相近的选区底上,选中即消失。
    /// </summary>
    internal const double MinForegroundDelta = 0.18;

    /// <summary>
    /// 把方案给的选区色整定成实际绘制用的填充色:强制不透明,并保证与
    /// <paramref name="background" /> 至少差 <see cref="MinLightnessDeltaFor" /> 那一档的 L*。
    /// </summary>
    /// <param name="selection">配色方案 / 用户设置里的选区色(其 alpha 被忽略)。</param>
    /// <param name="background">终端默认背景色。</param>
    public static Rgba Fill(Rgba selection, Rgba background)
    {
        double backgroundLightness = Lightness(background);
        bool onDark = backgroundLightness < 50;
        double minDelta = onDark ? MinLightnessDeltaOnDark : MinLightnessDeltaOnLight;
        if (Math.Abs(Lightness(selection) - backgroundLightness) >= minDelta)
        {
            return Opaque(selection);
        }

        // 推的方向由背景明暗定,不由选区色定:暗底上把选区提亮、亮底上把它压深,才是
        // 各家终端一致的观感。混合目标取纯白/纯黑,是最省事又能保住原色相的 tint/shade。
        byte target = onDark ? (byte)0xFF : (byte)0x00;

        // 二分出**刚好够**的混合比例:够看见就停手,不多推一分。上界 1.0 必然满足
        // (背景 L* < 50 时推到白至少差 50,反之推到黑至少差 50,两档阈值都在 50 以内),
        // 所以搜索必然收敛。
        double low = 0.0;
        double high = 1.0;
        for (int i = 0; i < 12; i++)
        {
            double mid = (low + high) / 2;
            if (Math.Abs(Lightness(Mix(selection, target, mid)) - backgroundLightness) >= minDelta)
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }
        return Opaque(Mix(selection, target, high));
    }

    /// <summary>
    /// 撞上选区底的前景要推到的 WCAG 对比度。
    /// </summary>
    /// <remarks>
    /// 与 ANSI 颜色压在自家终端底上的要求同一档(<c>UiThemeCatalogTests</c> 的
    /// <c>MinimumGlyphContrast</c>,AA 的图形与界面组件档):选中之后的字不该比没选中时更难认。
    /// 只推到 <see cref="MinForegroundDelta" /> 那道亮度差是不够的 —— 对着内置方案量过,
    /// 那样推完只有 1.55:1 ~ 2.3:1,看得见却费眼。
    /// </remarks>
    internal const double MinForegroundContrast = 3.0;

    /// <summary>
    /// 前景与选区底撞明度时,<b>保住色相、只调明度</b>:往白(或黑)里混,混到与选区底的 WCAG 对比度
    /// 够 <see cref="MinForegroundContrast" /> 就停手。本来就够的原样返回。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 此前撞上的格一律换成同一个兜底色(终端默认前景,它也撞就纯白 / 纯黑):读得清了,可选中一段
    /// <c>ls --color</c> 的目录蓝、可执行绿、压缩包红就全成了一个颜色 —— 用户拖选正是想看清这一段。
    /// 往白 / 黑里混不改色相(三个通道同比例地向同一端靠),推的量又只到够看清为止。
    /// </para>
    /// <para>
    /// <b>往前景本来所在的那一侧推</b>(比选区底亮就提亮、暗就压暗),改动最小;那一侧推到头也够不着
    /// (选区底本身已经贴着纯黑 / 纯白)才换到另一侧 —— 两侧里总有一侧够得着 4.5:1 以上。
    /// </para>
    /// <para>
    /// 走的是带 <c>Math.Pow</c> 的相对亮度与二分,不便宜:渲染层只在 <see cref="Brightness" />
    /// 判出「撞了」之后才调,并按前景色缓存结果(撞色的前景就那么几种)。
    /// </para>
    /// </remarks>
    /// <param name="foreground">格子本来要画的前景色。</param>
    /// <param name="fill">整定后的选区填充色(<see cref="Fill" /> 的结果)。</param>
    public static Rgba ReadableOnSelection(Rgba foreground, Rgba fill)
    {
        foreground = Opaque(foreground);
        double fillLuminance = RelativeLuminance(fill);
        double luminance = RelativeLuminance(foreground);
        if (ContrastRatio(luminance, fillLuminance) >= MinForegroundContrast)
        {
            return foreground;
        }
        bool whiteReaches = ContrastRatio(1.0, fillLuminance) >= MinForegroundContrast;
        bool blackReaches = ContrastRatio(0.0, fillLuminance) >= MinForegroundContrast;
        bool up = luminance >= fillLuminance ? whiteReaches || !blackReaches : !blackReaches;
        byte target = up ? (byte)0xFF : (byte)0x00;

        // 二分出刚好够的混合比例;上界 1.0(纯白 / 纯黑)按上面的选边必然够得着。
        double low = 0.0;
        double high = 1.0;
        for (int i = 0; i < 14; i++)
        {
            double mid = (low + high) / 2;
            if (ContrastRatio(RelativeLuminance(Mix(foreground, target, mid)), fillLuminance) >= MinForegroundContrast)
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }
        return Opaque(Mix(foreground, target, high));
    }

    /// <summary>WCAG 2 的对比度:<c>(亮 + 0.05) / (暗 + 0.05)</c>。</summary>
    internal static double ContrastRatio(double luminanceA, double luminanceB) =>
        (Math.Max(luminanceA, luminanceB) + 0.05) / (Math.Min(luminanceA, luminanceB) + 0.05);

    /// <summary>WCAG 2 的相对亮度(0–1,sRGB 线性化后的 Rec.709 加权)。</summary>
    internal static double RelativeLuminance(Rgba color) =>
        (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    /// <summary>
    /// 感知亮度(0–1)。用的是 <c>0.299/0.587/0.114</c> 这套直接压在 sRGB 通道上的老式加权,
    /// 而非 <see cref="Lightness" /> 那条要开方的正经路子:它在**每个选中格**上都要算一次,
    /// 精度够用即可,不能带 <c>Math.Pow</c> 进渲染热路径。
    /// </summary>
    public static double Brightness(Rgba color) =>
        ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255.0;

    /// <summary>CIE L*(0–100)。只在换主题 / 换配色时算,可以走完整的 sRGB 线性化。</summary>
    internal static double Lightness(Rgba color)
    {
        double y = RelativeLuminance(color);
        return y > 0.008856 ? (116 * Math.Cbrt(y)) - 16 : 903.3 * y;
    }

    private static double Linear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static Rgba Mix(Rgba color, byte target, double t) =>
        Rgba.FromRgb(Lerp(color.R, target, t), Lerp(color.G, target, t), Lerp(color.B, target, t));

    private static byte Lerp(byte from, byte to, double t) =>
        (byte)Math.Clamp(Math.Round(from + ((to - from) * t)), 0, 255);

    private static Rgba Opaque(Rgba color) => Rgba.FromRgb(color.R, color.G, color.B);
}
