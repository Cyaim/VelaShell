using System.Text.Json;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 出厂界面语言必须是「跟随系统」。
/// </summary>
/// <remarks>
/// 当初出厂值是 <c>zh-CN</c>,非中文环境下全新安装也是一屏中文 —— AppImage 目录因此
/// 不收录(appimage.github.io#8573)。用户在设置里选过的语言仍然优先。
/// </remarks>
[TestClass]
[TestCategory("DataStore")]
public class LanguageDefaultsTests
{
    private static readonly JsonSerializerOptions CamelCaseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [TestMethod]
    public void FreshSettings_FollowTheSystem() =>
        Assert.AreEqual(string.Empty, new AppSettings().Language);

    [TestMethod]
    public void AStoredLanguage_IsKeptAsIs()
    {
        // 存量配置里写着的语言分不清是用户选的还是旧出厂值,一律照旧生效
        AppSettings reloaded = JsonSerializer.Deserialize<AppSettings>("""{"language":"zh-CN"}""", CamelCaseJson)!;
        reloaded.Normalize();

        Assert.AreEqual("zh-CN", reloaded.Language);
    }
}
