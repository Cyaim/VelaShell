using System.Globalization;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>连接编辑框「服务器时区」下拉里的一项。</summary>
/// <param name="Minutes">服务器时钟的 UTC 偏移(分钟);null = 与本机相同。</param>
/// <param name="Label">显示的文字。</param>
public sealed record FtpTimeZoneOption(int? Minutes, string Label)
{
    /// <summary>
    /// 现实中用到的时区偏移(分钟),从 UTC−12:00 到 UTC+14:00,含 :30 / :45 的那几个(印度、尼泊尔、纽芬兰、查塔姆…)。
    /// </summary>
    private static readonly int[] KnownOffsets =
    [
        -720, -660, -600, -570, -540, -480, -420, -360, -300, -240, -210, -180, -120, -60,
        0, 60, 120, 180, 210, 240, 270, 300, 330, 345, 360, 390, 420, 480, 525, 540, 570,
        600, 630, 660, 720, 765, 780, 840,
    ];

    /// <summary>ComboBox 按它显示。</summary>
    public override string ToString() => Label;

    /// <summary>
    /// 下拉候选:「与本机相同」+ 全部已知偏移;<paramref name="current" /> 不在表里(手改过的配置)时按大小插进去,
    /// 免得一打开编辑框就被悄悄改成别的值。
    /// </summary>
    /// <param name="current">配置里现存的偏移;null = 与本机相同。</param>
    public static IReadOnlyList<FtpTimeZoneOption> Build(int? current)
    {
        var minutes = new SortedSet<int>(KnownOffsets);
        if (current is { } value and >= FtpSettings.MinServerUtcOffsetMinutes and <= FtpSettings.MaxServerUtcOffsetMinutes)
        {
            minutes.Add(value);
        }
        return [new(null, Strings.Get("Ftp_ServerTimeZoneLocal")), .. minutes.Select(static m => new FtpTimeZoneOption(m, Format(m)))];
    }

    /// <summary><c>UTC+05:45</c> 这种写法;0 写成 <c>UTC+00:00</c>,与系统时区列表的读法一致。</summary>
    internal static string Format(int minutes)
    {
        char sign = minutes < 0 ? '−' : '+';
        int abs = Math.Abs(minutes);
        return string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{abs / 60:00}:{abs % 60:00}");
    }
}
