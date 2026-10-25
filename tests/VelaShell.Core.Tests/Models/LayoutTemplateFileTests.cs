using System.Text.Json;
using VelaShell.Core.Import;
using VelaShell.Core.Layouts;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>布局模板的导出文件:带格式头与版本、读回来原样(含不认识的字段),别的 JSON 一律认作「不是布局模板文件」。</summary>
[TestClass]
public sealed class LayoutTemplateFileTests
{
    private static LayoutTemplate Sample(string name) => new()
    {
        Name = name,
        CreatedAt = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero),
        Root = new()
        {
            Orientation = LayoutOrientation.Vertical,
            Children =
            [
                new() { Proportion = 0.3, Documents = [new() { Kind = LayoutDocumentKind.Terminal, ProfileId = Guid.NewGuid(), Title = "root@web-1", IsPinned = true }] },
                new() { Documents = [new() { Kind = LayoutDocumentKind.LocalShell, ShellId = "pwsh", Title = "PowerShell" }], TabsPosition = "Left" },
            ],
        },
    };

    [TestMethod]
    public void Export_WritesTheHeader_AndParseReadsTheSameTemplatesBack()
    {
        LayoutTemplate a = Sample("机房 A");
        LayoutTemplate b = Sample("机房 B");

        string json = LayoutTemplateFile.Export([a, b], new DateTime(2026, 10, 25, 1, 2, 3, DateTimeKind.Utc));

        using (JsonDocument doc = JsonDocument.Parse(json))
        {
            JsonElement header = doc.RootElement.GetProperty(SettingsTransfer.HeaderProperty);
            Assert.AreEqual(LayoutTemplateFile.FormatId, header.GetProperty("format").GetString());
            Assert.AreEqual(LayoutTemplateFile.CurrentVersion, header.GetProperty("version").GetInt32());
            Assert.AreEqual(2, header.GetProperty("count").GetInt32());
        }
        StringAssert.Contains(json, "机房 A", "中文原样写,不转义成 \\u");
        IReadOnlyList<LayoutTemplate> back = LayoutTemplateFile.Parse(json)!;
        Assert.AreSequenceEqual(new[] { a.Id, b.Id }, back.Select(t => t.Id).ToArray(), "Id 跟着走:同一个模板导到另一台同步设备上还是同一个");
        Assert.AreEqual(a.UpdatedAt, back[0].UpdatedAt);
        Assert.AreEqual(0.3, back[0].Root!.Children[0].Proportion);
        Assert.IsNull(back[0].Root!.Children[1].Proportion);
        Assert.AreEqual("pwsh", back[0].Root!.Children[1].Documents[0].ShellId);
        Assert.IsTrue(back[0].Root!.Children[0].Documents[0].IsPinned);
    }

    [TestMethod]
    public void FieldsFromANewerVersion_SurviveTheRoundTrip()
    {
        const string json = """
            {
              "velaShellExport": { "format": "velashell-layout-templates", "version": 9 },
              "templates": [
                {
                  "id": "6f1c1b8e-1d7b-4c2e-9a55-0c3f7f3e5a10", "name": "future", "schemaVersion": 4, "color": "teal",
                  "root": { "documents": [ { "kind": "hologram", "title": "x", "beam": 3 }, { "kind": "localShell", "shellId": "cmd", "title": "cmd" } ], "glow": true }
                }
              ]
            }
            """;

        LayoutTemplate template = LayoutTemplateFile.Parse(json)!.Single();
        string again = LayoutTemplateFile.Export([template], DateTime.UtcNow);

        Assert.AreEqual(4, template.SchemaVersion, "版本更高也尽力读");
        StringAssert.Contains(again, "\"color\": \"teal\"", "模板上不认识的字段原样写回去");
        StringAssert.Contains(again, "\"glow\": true", "节点上的也是");
        StringAssert.Contains(again, "\"beam\": 3", "标签上的也是");
        StringAssert.Contains(again, "hologram", "不认识的标签类型留着,打开时才跳过");
    }

    [TestMethod]
    [DataRow("not json at all")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("""{"templates": []}""")]
    [DataRow("""{"velaShellExport": {"format": "velashell-settings", "version": 2}, "templates": []}""")]
    [DataRow("""{"velaShellExport": {"format": "velashell-layout-templates"}, "templates": {}}""")]
    [DataRow("""{"velaShellExport": {"format": 5}, "templates": []}""")]
    public void AnythingElse_IsNotALayoutTemplateFile(string json) =>
        Assert.IsNull(LayoutTemplateFile.Parse(json));

    [TestMethod]
    public void ASettingsExport_IsNotALayoutTemplateFile() =>
        Assert.IsNull(LayoutTemplateFile.Parse(SettingsTransfer.Export(new AppSettings(), ["general"], DateTime.UtcNow)));

    [TestMethod]
    public void UnusableEntries_AreDropped_AndTheRestTidied()
    {
        string longName = new('长', LayoutTemplate.MaxNameLength + 20);
        string json = """
            {
              "velaShellExport": { "format": "velashell-layout-templates", "version": 1 },
              // 文件是给人改的:注释与尾逗号都认
              "templates": [
                null,
                { "name": "   ", "root": { "documents": [ { "kind": "terminal", "title": "a" } ] } },
                { "name": "empty tree", "root": { "documents": [] } },
                { "name": "no root" },
                { "id": "00000000-0000-0000-0000-000000000000", "name": "  zero id  ", "root": { "documents": [ { "kind": "terminal", "title": "b" } ] } },
                { "name": "LONG", "root": { "documents": [ { "kind": "terminal", "title": "c" } ] } },
              ],
            }
            """.Replace("LONG", longName, StringComparison.Ordinal);

        IReadOnlyList<LayoutTemplate> parsed = LayoutTemplateFile.Parse(json)!;

        Assert.HasCount(2, parsed);
        Assert.AreEqual("zero id", parsed[0].Name, "名字去首尾空白");
        Assert.AreNotEqual(Guid.Empty, parsed[0].Id, "全零的 Id 换一个新的");
        Assert.AreEqual(LayoutTemplate.MaxNameLength, parsed[1].Name.Length, "超长的名字截断");
    }
}
