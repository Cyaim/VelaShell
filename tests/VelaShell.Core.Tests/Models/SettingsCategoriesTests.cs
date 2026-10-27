using System.Reflection;
using System.Text.Json;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 设置分类的切法:每个根级字段恰好归一类,登记的特殊字段都真实存在。
/// </summary>
/// <remarks>
/// 漏归类的字段既不随导出走,也不在审计「设置变更」里出现,而且没有任何地方报错;
/// 字段改了名、登记表没跟着改,机密就会原样写进导出文件。两件事都只能靠这里钉住。
/// </remarks>
[TestClass]
[TestCategory("Settings")]
public sealed class SettingsCategoriesTests
{
    private static string Json(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    [TestMethod]
    public void EveryAppSettingsProperty_BelongsToExactlyOneCategory()
    {
        string[] properties =
        [
            .. typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .Select(p => Json(p.Name))
        ];
        foreach (string property in properties)
        {
            SettingsCategory[] owners =
            [
                .. SettingsCategories.All.Where(c => c.Block == property || c.TopLevel.Contains(property))
            ];
            Assert.HasCount(1, owners, $"AppSettings.{property} 应恰好归到一个分类,实际 {owners.Length} 个");
        }
        string[] declared = [.. SettingsCategories.All.SelectMany(c => c.TopLevel.Prepend(c.Block))];
        Assert.IsEmpty(declared.Except(properties).ToArray(), "分类里登记了 AppSettings 上并不存在的字段");
    }

    [TestMethod]
    public void EveryBlock_IsAnOptionsObject()
    {
        foreach (SettingsCategory category in SettingsCategories.All)
        {
            PropertyInfo property = typeof(AppSettings).GetProperties().Single(p => Json(p.Name) == category.Block);
            Assert.IsTrue(typeof(ObservableOptions).IsAssignableFrom(property.PropertyType),
                $"{category.Id} 的分块 {category.Block} 应是一块分组选项");
        }
    }

    [TestMethod]
    public void SecretStateAndMigrationFields_AllExist()
    {
        JsonElement json = JsonSerializer.SerializeToElement(new AppSettings(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        foreach ((string block, string field) in SettingsCategories.SecretFields
                     .Concat(SettingsCategories.StateFields)
                     .Concat(SettingsCategories.MigrationFields))
        {
            Assert.IsTrue(json.TryGetProperty(block, out JsonElement element) && element.TryGetProperty(field, out _),
                $"{block}.{field} 不存在 —— 字段改名之后登记表没跟上");
        }
    }

    /// <summary>
    /// 分块里凡是叫 <c>…Migrated</c> 的都是 <see cref="AppSettings.Normalize" /> 打的一次性标记:漏登记的那个,
    /// 第一次保存时从 false 变成 true,审计就凭空记一条「设置变更」(自动下载更新的迁移标记就这样漏过一次)。
    /// </summary>
    [TestMethod]
    public void EveryMigratedFlag_IsRegisteredAsAMigrationField()
    {
        (string Block, string Field)[] flags =
        [
            .. typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => typeof(ObservableOptions).IsAssignableFrom(p.PropertyType))
                .SelectMany(block => block.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.PropertyType == typeof(bool) && p.Name.EndsWith("Migrated", StringComparison.Ordinal))
                    .Select(p => (Json(block.Name), Json(p.Name))))
        ];
        Assert.IsNotEmpty(flags);
        Assert.IsEmpty(flags.Except(SettingsCategories.MigrationFields).ToArray(),
            "有迁移标记没登记进 SettingsCategories.MigrationFields");
    }

    [TestMethod]
    public void OnlyProxyAndSecurity_AreSensitive()
    {
        Assert.AreSequenceEqual(
            [SettingsCategories.Security, SettingsCategories.Proxy],
            [.. SettingsCategories.All.Where(c => c.IsSensitive).Select(c => c.Id)]);
    }

    [TestMethod]
    public void Changed_ListsTheCategoriesThatDiffer_InDisplayOrder()
    {
        var before = new AppSettings();
        var after = new AppSettings { Theme = "light", ScrollbackLines = 500 };
        after.Security.ConfirmExternalLaunch = false;

        Assert.AreSequenceEqual(
            [SettingsCategories.Appearance, SettingsCategories.Terminal, SettingsCategories.Security],
            [.. SettingsCategories.Changed(before, after).Select(c => c.Id)]);
    }

    [TestMethod]
    public void Changed_IgnoresRuntimeStateAndMigrationMarks()
    {
        var before = new AppSettings();
        var after = new AppSettings();
        after.Appearance.LastWindowWidth = 1600;
        after.Appearance.LastWindowX = 20;
        after.General.LastOpenProfileIds = [Guid.NewGuid()];
        after.Security.RecordingOptInMigrated = true;
        after.Proxy.DefaultsMigrated = true;

        Assert.IsEmpty(SettingsCategories.Changed(before, after));
    }

    [TestMethod]
    public void IdsAreUnique_AndFindIgnoresCase()
    {
        Assert.HasCount(SettingsCategories.All.Count, SettingsCategories.All.Select(c => c.Id).Distinct());
        Assert.AreEqual(SettingsCategories.Proxy, SettingsCategories.Find("PROXY")?.Id);
        Assert.IsNull(SettingsCategories.Find("plugins"), "更新版本写的分类认不出时为 null");
    }
}
