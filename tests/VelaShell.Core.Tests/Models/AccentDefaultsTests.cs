using System.Text.Json;
using VelaShell.Core.Models;
using VelaShell.Core.Services;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 出厂强调色必须是「跟随主题」。
/// </summary>
/// <remarks>
/// 强调色覆盖的优先级高于主题令牌(<c>App.ApplyAccent</c> 遮蔽 <c>VelaAccent</c> 三件套),
/// 出厂给一个具体色值,全新安装下十二套主题各自的强调色就全被它盖住了 —— 当初出厂值是
/// <c>#E91E63</c>,选哪套主题都是同一个粉色。
/// </remarks>
[TestClass]
[TestCategory("DataStore")]
public class AccentDefaultsTests
{
    private static readonly JsonSerializerOptions CamelCaseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [TestMethod]
    public void FreshSettings_FollowTheTheme()
    {
        Assert.AreEqual(string.Empty, new AppSettings().AccentColor);
        Assert.IsNull(new ThemeService("nord", new AppSettings().AccentColor).AccentColor,
            "空串要落成「无覆盖」,主题自己的强调色才露得出来");
    }

    [TestMethod]
    public void AStoredAccent_IsKeptAsIs()
    {
        // 存量配置里写着的色值分不清是用户选的还是旧出厂值,一律照旧生效
        AppSettings reloaded = JsonSerializer.Deserialize<AppSettings>("""{"accentColor":"#E91E63"}""", CamelCaseJson)!;
        reloaded.Normalize();

        Assert.AreEqual("#E91E63", reloaded.AccentColor);
    }
}
