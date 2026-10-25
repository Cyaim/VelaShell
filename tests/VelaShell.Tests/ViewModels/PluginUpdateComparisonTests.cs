using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 插件管理页「有没有新版」的判定:按 SDK <c>SemanticVersion</c> 的完整优先级比(与应用自更新、vela-plugin、商店同一份)。
/// <para>
/// 装载闸(minHostVersion / minSdkVersion)只比数字段,这里不是 —— 装着 <c>1.2.0-beta.1</c> 时必须看得到商店上的 <c>1.2.0</c>。
/// 任一侧解析不出就是「比不了」:不报有新版,也不冒充已是最新。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class PluginUpdateComparisonTests
{
    [TestMethod]
    [DataRow("1.2.0", "1.2.0-beta.1", null, DisplayName = "正式版高于同号预发布")]
    [DataRow("1.0.0-beta.10", "1.0.0-beta.2", null, DisplayName = "预发布按数值比:beta.10 比 beta.2 新")]
    [DataRow("1.0.0-beta.2", "1.0.0-beta.10", PluginUpdateState.UpToDate)]
    [DataRow("1.0.0", "1.0.0", PluginUpdateState.UpToDate)]
    [DataRow("1.0.0+build.9", "1.0.0", PluginUpdateState.UpToDate, DisplayName = "构建元数据不参与比较")]
    [DataRow("1.2.3.1", "1.2.3", null, DisplayName = "第四段参与比较")]
    [DataRow("1.0.0", "1.0.0-beta.01", PluginUpdateState.Unknown, DisplayName = "本地版本号排不了序:不报有新版")]
    [DataRow("2147483648.0.0", "1.0.0", PluginUpdateState.Unknown, DisplayName = "商店版本号超出范围:不当成正式版")]
    [DataRow(null, "1.0.0", PluginUpdateState.Unknown)]
    public void CompareForUpdate_UsesFullSemVerPrecedence(string? remote, string local, PluginUpdateState? expected)
    {
        Assert.AreEqual(expected, PluginManagerViewModel.CompareForUpdate(remote, local));
    }
}
