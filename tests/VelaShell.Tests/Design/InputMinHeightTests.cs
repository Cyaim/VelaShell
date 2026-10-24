using System.Globalization;
using System.Xml.Linq;

namespace VelaShell.Tests.Design;

/// <summary>
/// 守门:比 Fluent 最小高度还矮的 <c>Height</c> 必须连同 <c>MinHeight</c> 一起写。
/// </summary>
/// <remarks>
/// <para>
/// Fluent 主题给 TextBox / ComboBox / CheckBox / RadioButton 这一类控件设了 <c>MinHeight="32"</c>,而 Avalonia
/// 排版时 <c>MinHeight</c> 压过 <c>Height</c> —— 只写一个 <c>Height="26"</c>,界面上照样是 32,没有任何告警。
/// 资源管理器的过滤框与快捷命令区的搜索框都按 26 写、实际 32,比同类的搜索框(28)高出一截;
/// 两个路径编辑框按 22 写、实际 32,一进编辑整排跳高(同一排的 ComboBox 写了 <c>MinHeight="0"</c>,只有它俩漏了)。
/// </para>
/// <para>
/// 同一份扫描也看样式:<c>Setter Property="Height"</c> 比 32 矮、同一个样式里却没有 <c>MinHeight</c> 的,同样不生效。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Design")]
public sealed class InputMinHeightTests
{
    /// <summary>Fluent 主题里最小高度为 32 的控件。</summary>
    private static readonly HashSet<string> ThemedMinHeightControls =
    [
        "TextBox", "MaskedTextBox", "AutoCompleteBox", "NumericUpDown",
        "ComboBox", "CheckBox", "RadioButton", "CalendarDatePicker"
    ];

    private const double FluentMinHeight = 32;

    [TestMethod]
    public void HeightBelowTheThemeMinimum_AlsoSetsMinHeight()
    {
        string srcRoot = Path.Combine(RepoRoot(), "src");
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(srcRoot, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }
            string relative = Path.GetRelativePath(srcRoot, file).Replace('\\', '/');
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);

            foreach (XElement element in document.Descendants().Where(e => ThemedMinHeightControls.Contains(e.Name.LocalName)))
            {
                if (IsBelowMinimum((string?)element.Attribute("Height")) && element.Attribute("MinHeight") is null)
                {
                    offenders.Add($"{relative}:{((System.Xml.IXmlLineInfo)element).LineNumber}  <{element.Name.LocalName} Height=\"{(string?)element.Attribute("Height")}\">");
                }
            }

            foreach (XElement style in document.Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                string selector = (string?)style.Attribute("Selector") ?? "";
                if (selector.Contains("/template/", StringComparison.Ordinal) || !TargetsThemedControl(selector))
                {
                    continue;
                }
                XElement[] setters = [.. style.Elements().Where(e => e.Name.LocalName == "Setter")];
                string? height = setters.FirstOrDefault(s => (string?)s.Attribute("Property") == "Height")?.Attribute("Value")?.Value;
                bool hasMinHeight = setters.Any(s => (string?)s.Attribute("Property") == "MinHeight");
                if (IsBelowMinimum(height) && !hasMinHeight)
                {
                    offenders.Add($"{relative}:{((System.Xml.IXmlLineInfo)style).LineNumber}  <Style Selector=\"{selector}\"> Height={height}");
                }
            }
        }

        Assert.IsEmpty(
            offenders,
            $"这些输入控件的 Height 比 Fluent 的最小高度 {FluentMinHeight} 还矮,却没写 MinHeight —— 实际渲染成 {FluentMinHeight}。"
            + "把 MinHeight 一起写上(与 Height 相同,或 0):"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    private static bool IsBelowMinimum(string? height) =>
        height is not null
        && double.TryParse(height, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        && value < FluentMinHeight;

    /// <summary>选择器的末段(真正被设样式的那个元素)是不是这一类控件。</summary>
    private static bool TargetsThemedControl(string selector)
    {
        foreach (string alternative in selector.Split(','))
        {
            string last = alternative.Trim().Split([' ', '>'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            int end = last.IndexOfAny(['.', ':', '#', '[', '/']);
            string type = end < 0 ? last : last[..end];
            if (ThemedMinHeightControls.Contains(type))
            {
                return true;
            }
        }
        return false;
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到仓库根目录(找不到 VelaShell.slnx)。");
    }
}
