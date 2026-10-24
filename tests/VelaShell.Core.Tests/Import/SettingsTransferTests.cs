using System.Text.Json;
using System.Text.Json.Nodes;
using VelaShell.Core.Import;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Import;

/// <summary>
/// 设置导出文件:只带勾选的分类、机密一律不写;导入只覆盖文件里带着且勾上的分类,旧的整份导出照旧能导。
/// </summary>
[TestClass]
[TestCategory("Settings")]
public sealed class SettingsTransferTests
{
    private const string ProxyPassword = "hunter2-should-never-leave-this-machine";
    private const string WebhookUrl = "https://oapi.dingtalk.com/robot/send?access_token=s3cr3t-token";

    private static readonly DateTime ExportedAt = new(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] AllIds = [.. SettingsCategories.All.Select(c => c.Id)];

    [TestMethod]
    public void Export_WritesOnlyTheSelectedCategories_AndAHeader()
    {
        string json = SettingsTransfer.Export(Configured(), [SettingsCategories.Appearance, SettingsCategories.Terminal], ExportedAt);

        JsonObject root = JsonNode.Parse(json)!.AsObject();
        Assert.AreSequenceEqual(
            [SettingsTransfer.HeaderProperty, "theme", "accentColor", "terminalFont", "terminalFontSize", "scrollbackLines",
             "terminalType", "terminalEncoding", "appearance", "terminalBehavior"],
            [.. root.Select(p => p.Key)],
            SequenceOrder.InAnyOrder);
        JsonObject header = root[SettingsTransfer.HeaderProperty]!.AsObject();
        Assert.AreEqual(SettingsTransfer.FormatId, (string?)header["format"]);
        Assert.AreEqual(SettingsTransfer.CurrentVersion, (int?)header["version"]);
        Assert.AreSequenceEqual(["appearance", "terminal"], [.. header["categories"]!.AsArray().Select(n => (string?)n)]);
        Assert.DoesNotContain("proxy.example.com", json, "没勾代理,代理的地址就不该在文件里");
    }

    [TestMethod]
    public void Export_NeverWritesSecrets_EvenWhenTheirCategoriesAreTicked()
    {
        string json = SettingsTransfer.Export(Configured(), AllIds, ExportedAt);

        Assert.DoesNotContain(ProxyPassword, json, "代理密码被写进导出文件了");
        Assert.DoesNotContain("s3cr3t-token", json, "Webhook 地址(本身就是令牌)被写进导出文件了");
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        Assert.IsFalse(root["proxy"]!.AsObject().ContainsKey("password"));
        Assert.IsFalse(root["security"]!.AsObject().ContainsKey("webhookUrl"));
        Assert.AreEqual("proxy.example.com", (string?)root["proxy"]!["host"], "勾了代理,地址照常写");
    }

    [TestMethod]
    public void Export_LeavesOutRuntimeState_AndDoesNotTouchTheSource()
    {
        AppSettings settings = Configured();
        string json = SettingsTransfer.Export(settings, AllIds, ExportedAt);

        JsonObject root = JsonNode.Parse(json)!.AsObject();
        Assert.IsFalse(root["appearance"]!.AsObject().ContainsKey("lastWindowWidth"), "窗口尺寸是运行状态,不是设置");
        Assert.IsFalse(root["general"]!.AsObject().ContainsKey("lastOpenProfileIds"));
        Assert.IsTrue(root["security"]!.AsObject().ContainsKey("recordingOptInMigrated"), "迁移标记要带着,否则导入时会再迁移一遍");
        Assert.AreEqual(ProxyPassword, settings.Proxy.Password, "抹的应是副本");
        Assert.AreEqual(WebhookUrl, settings.Security.WebhookUrl);
    }

    [TestMethod]
    public void Import_ReplacesOnlyTheTickedCategoriesThatTheFileCarries()
    {
        AppSettings source = Configured();
        source.Theme = "light";
        source.TerminalFontSize = 20;
        source.General.ConnectTimeoutSeconds = 42;
        SettingsImportFile file = SettingsTransfer.Parse(SettingsTransfer.Export(source, [SettingsCategories.Appearance, SettingsCategories.General], ExportedAt))!;
        Assert.AreSequenceEqual([SettingsCategories.General, SettingsCategories.Appearance], [.. file.Categories.Select(c => c.Id)]);
        Assert.IsFalse(file.IsLegacy);

        var local = new AppSettings { Theme = "dark", TerminalFontSize = 13 };
        local.General.ConnectTimeoutSeconds = 7;
        SettingsImportMerge merge = SettingsTransfer.Merge(local, file, [SettingsCategories.Appearance, SettingsCategories.Terminal]);

        Assert.AreSequenceEqual([SettingsCategories.Appearance], [.. merge.Applied.Select(c => c.Id)], "终端不在文件里,常规没勾");
        Assert.AreEqual("light", merge.Settings.Theme);
        Assert.AreEqual(13, merge.Settings.TerminalFontSize, "文件里没带终端,本机的字号不动");
        Assert.AreEqual(7, merge.Settings.General.ConnectTimeoutSeconds, "常规没勾,不覆盖");
        Assert.AreEqual(13, local.TerminalFontSize, "合并不改传进来的那份");
    }

    [TestMethod]
    public void Import_KeepsLocalRuntimeState()
    {
        AppSettings source = Configured();
        source.Appearance.LastWindowWidth = 640;
        // 手写的文件(或别的工具写的)里也可能带着窗口尺寸:照样不采用。
        string json = JsonSerializer.Serialize(new { appearance = new { lastWindowWidth = 640, uiFontSize = 15 } });
        var local = new AppSettings();
        local.Appearance.LastWindowWidth = 1600;

        SettingsImportMerge merge = SettingsTransfer.Merge(local, SettingsTransfer.Parse(json)!, AllIds);

        Assert.AreEqual(1600, merge.Settings.Appearance.LastWindowWidth);
        Assert.AreEqual(15, merge.Settings.Appearance.UiFontSize);
    }

    [TestMethod]
    public void Import_OfALegacyFullExport_StillWorks()
    {
        // 本版本之前的导出:整份 AppSettings、camelCase、代理密码抹成空串、Webhook 地址原样带着。
        AppSettings old = Configured();
        old.Proxy.Password = string.Empty;
        old.General.ConnectTimeoutSeconds = 42;
        string json = JsonSerializer.Serialize(old, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });

        SettingsImportFile file = SettingsTransfer.Parse(json)!;
        Assert.IsTrue(file.IsLegacy);
        Assert.AreSequenceEqual(AllIds, [.. file.Categories.Select(c => c.Id)], "旧的整份导出带着全部分类");

        SettingsImportMerge merge = SettingsTransfer.Merge(new AppSettings(), file, AllIds);
        Assert.AreEqual(42, merge.Settings.General.ConnectTimeoutSeconds);
        Assert.AreEqual(WebhookUrl, merge.Settings.Security.WebhookUrl, "文件里明写着的 Webhook 地址照用");
        Assert.IsTrue(merge.ProxyPasswordMissing, "代理要认证、密码没带、本机又对不上 —— 要重填");
        Assert.IsFalse(merge.WebhookUrlMissing);
    }

    [TestMethod]
    public void Import_KeepsTheLocalProxyPassword_OnlyForTheSameProxy()
    {
        SettingsImportFile file = SettingsTransfer.Parse(SettingsTransfer.Export(Configured(), [SettingsCategories.Proxy], ExportedAt))!;

        SettingsImportMerge same = SettingsTransfer.Merge(Configured(), file, [SettingsCategories.Proxy]);
        Assert.AreEqual(ProxyPassword, same.Settings.Proxy.Password, "同一台代理换了台机器:留着本机的密码");
        Assert.IsFalse(same.ProxyPasswordMissing);

        AppSettings elsewhere = Configured();
        elsewhere.Proxy.Host = "proxy.attacker.example";
        SettingsImportMerge other = SettingsTransfer.Merge(elsewhere, file, [SettingsCategories.Proxy]);
        Assert.AreEqual(string.Empty, other.Settings.Proxy.Password, "文件指向另一台代理:本机的密码不能发给它");
        Assert.AreEqual("proxy.example.com", other.Settings.Proxy.Host);
        Assert.IsTrue(other.ProxyPasswordMissing);
    }

    [TestMethod]
    public void Import_KeepsTheLocalWebhookUrl_WhenTheFileHasNone()
    {
        SettingsImportFile file = SettingsTransfer.Parse(SettingsTransfer.Export(Configured(), [SettingsCategories.Security], ExportedAt))!;

        SettingsImportMerge kept = SettingsTransfer.Merge(Configured(), file, [SettingsCategories.Security]);
        Assert.AreEqual(WebhookUrl, kept.Settings.Security.WebhookUrl);
        Assert.IsFalse(kept.WebhookUrlMissing);

        SettingsImportMerge fresh = SettingsTransfer.Merge(new AppSettings(), file, [SettingsCategories.Security]);
        Assert.IsTrue(fresh.Settings.Security.AlertWebhook);
        Assert.IsTrue(fresh.WebhookUrlMissing, "打开了 Webhook 告警却没有地址:要重填");
    }

    [TestMethod]
    public void Import_IgnoresCategoriesThatWereNotTicked_EvenIfTheyAreInTheFile()
    {
        AppSettings source = Configured();
        source.Security.ConfirmExternalLaunch = false;
        SettingsImportFile file = SettingsTransfer.Parse(SettingsTransfer.Export(source, AllIds, ExportedAt))!;

        SettingsImportMerge merge = SettingsTransfer.Merge(new AppSettings(), file, [SettingsCategories.Appearance]);

        Assert.IsTrue(merge.Settings.Security.ConfirmExternalLaunch, "安全审计没勾:文件关掉的确认不能跟着进来");
        Assert.AreEqual("system", merge.Settings.Proxy.Type);
    }

    [TestMethod]
    public void Import_ToleratesCaseCommentsAndNullBlocks()
    {
        const string json = """
            {
              // 手改过的文件
              "Theme": "light",
              "XServer": null,
              "General": { "ConnectTimeoutSeconds": 99, },
            }
            """;
        SettingsImportFile file = SettingsTransfer.Parse(json)!;
        Assert.AreSequenceEqual([SettingsCategories.General, SettingsCategories.Appearance, SettingsCategories.XServer],
            [.. file.Categories.Select(c => c.Id)]);

        var local = new AppSettings();
        local.XServer.DisplayNumber = 3;
        SettingsImportMerge merge = SettingsTransfer.Merge(local, file, AllIds);

        Assert.AreEqual("light", merge.Settings.Theme);
        Assert.AreEqual(99, merge.Settings.General.ConnectTimeoutSeconds);
        Assert.AreEqual(new XServerOptions().DisplayNumber, merge.Settings.XServer.DisplayNumber, "写成 null 的分块取默认值");
    }

    [TestMethod]
    public void Parse_RejectsThingsThatAreNotSettings()
    {
        Assert.IsNull(SettingsTransfer.Parse("{ not json"));
        Assert.IsNull(SettingsTransfer.Parse("null"));
        Assert.IsNull(SettingsTransfer.Parse("[1, 2]"));
        Assert.IsNull(SettingsTransfer.Parse("{}"), "一个分类都没有:以前会被当成一份全默认的设置照单全收");
        Assert.IsNull(SettingsTransfer.Parse("""{ "name": "my-app", "version": "1.0.0" }"""), "选错的 package.json");
        Assert.IsNull(SettingsTransfer.Parse("""{ "general": 5 }"""), "类型对不上");
    }

    private static AppSettings Configured()
    {
        var settings = new AppSettings();
        settings.Proxy.Type = "socks5";
        settings.Proxy.Host = "proxy.example.com";
        settings.Proxy.Port = 1080;
        settings.Proxy.Username = "vela";
        settings.Proxy.Password = ProxyPassword;
        settings.Security.AlertWebhook = true;
        settings.Security.WebhookUrl = WebhookUrl;
        settings.Security.TrustedExternalLaunchTargets = ["ssh://root@10.0.0.1:22"];
        settings.Appearance.LastWindowWidth = 1280;
        settings.General.LastOpenProfileIds = [Guid.NewGuid()];
        settings.Normalize();
        return settings;
    }
}
