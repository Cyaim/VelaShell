using VelaShell.Core.Models;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 命令面板按会话的标签、主机与备注匹配(检索词不显示,只参与打分)。
/// </summary>
/// <remarks>
/// 标签原先只有编辑框与导入导出在读,面板只比标题 —— 打了 <c>prod</c> 标签的机器搜 prod 搜不到。
/// </remarks>
[TestClass]
[TestCategory("CommandPalette")]
public class PaletteKeywordTests
{
    private static int Score(string title, string query, params string[] keywords) =>
        PaletteScorer.Score(title, null, keywords, query, out _);

    [TestMethod]
    public void KeywordHit_MatchesWhenTheTitleDoesNot()
    {
        Assert.AreEqual(PaletteScorer.NoMatch, PaletteScorer.Score("db-01", null, "prod", out _));
        Assert.AreNotEqual(PaletteScorer.NoMatch, Score("db-01", "prod", "prod", "10.0.0.2"));
        Assert.AreNotEqual(PaletteScorer.NoMatch, Score("db-01", "值班", "10.0.0.2", "主库,值班:运维二组"), "备注的中间命中");
        Assert.AreEqual(PaletteScorer.NoMatch, Score("db-01", "zzz", "prod", "10.0.0.2"));
    }

    [TestMethod]
    public void KeywordHit_RanksBetweenTitleSubstringAndTitleSubsequence()
    {
        int titleSubstring = Score("api-prod-01", "prod");
        int tag = Score("db-01", "prod", "prod");
        int titleSubsequence = Score("pxrxoxd", "prod");

        Assert.IsGreaterThan(tag, titleSubstring, "名字里就有 prod 的最该在前");
        Assert.IsGreaterThan(titleSubsequence, tag, "用户自己打的标签比名字里散落着几个字母更像他要找的");
    }

    [TestMethod]
    public void EarlierKeywords_AndPrefixHits_WinAmongKeywordMatches()
    {
        Assert.IsGreaterThan(Score("b", "prod", "x", "production"), Score("a", "prod", "production"),
            "同为检索词前缀命中时,先列出的(标签在备注之前)权重更高");
        Assert.IsGreaterThan(Score("b", "prod", "nonprod"), Score("a", "prod", "prod-db"), "前缀命中高于中间命中");
    }

    [TestMethod]
    public void KeywordHit_HasNoTitleHighlight()
    {
        PaletteScorer.Score("db-01", null, ["prod"], "prod", out (int Start, int Length)[] spans);
        Assert.IsEmpty(spans, "标题里没有可高亮的字符");
    }

    [TestMethod]
    public void PaletteKeywords_AreTagsThenHostThenNotes()
    {
        var profile = new SessionProfile
        {
            Name = "db-01",
            Host = "10.0.0.2",
            Tags = ["prod", " ", "mysql"],
            Notes = "主库"
        };

        Assert.AreSequenceEqual(["prod", "mysql", "10.0.0.2", "主库"], [.. MainWindowViewModel.PaletteKeywords(profile)]);
        Assert.IsEmpty(MainWindowViewModel.PaletteKeywords(new SessionProfile()));
    }

    [TestMethod]
    public void Palette_FindsASessionByItsTag()
    {
        List<CommandPaletteItem> items =
        [
            new("会话", "db-01", () => { }, isSession: true, id: "a", keywords: ["prod"]),
            new("会话", "web-01", () => { }, isSession: true, id: "b", keywords: ["staging"]),
            new("命令", "打开设置", () => { }, id: "c")
        ];
        var vm = new CommandPaletteViewModel(() => items);
        vm.Open();

        vm.Query = "prod";

        Assert.AreEqual(1, vm.ResultCount);
        Assert.AreEqual("db-01", vm.SelectedItem!.Title);
    }
}
