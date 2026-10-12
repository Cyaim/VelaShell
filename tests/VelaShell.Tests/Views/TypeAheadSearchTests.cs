using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 键入定位的匹配规则(资源管理器同款)。接线由 <see cref="LocalPathPickerKeyboardUiTests" /> 盯。
/// </summary>
[TestClass]
public sealed class TypeAheadSearchTests
{
    // 合成的 ".." 行名字为 null:它永远不该被定位到。
    private static readonly string?[] Names = [null, "alpha.txt", "Fxa.txt", "fxyb.txt", "fxyc.txt", "gamma", "my file.txt"];

    [TestMethod]
    public void TypingAPrefix_NarrowsDownToTheItemThatStartsWithIt()
    {
        var search = new TypeAheadSearch(new ManualClock());
        int current = -1;
        List<int> path = [];
        foreach (string key in new[] { "f", "x", "y", "c" })
        {
            current = TypeAheadSearch.FindMatch(Names, current, search.Append(key)!);
            path.Add(current);
        }

        // f → 第一个 f 开头的;fx 时它还对得上,原地不动;fxy → fxyb;fxyc → fxyc。
        Assert.AreSequenceEqual([2, 2, 3, 4], path);
    }

    [TestMethod]
    public void MatchingIgnoresCase()
    {
        Assert.AreEqual(2, TypeAheadSearch.FindMatch(Names, -1, "FXA"));
        Assert.AreEqual(5, TypeAheadSearch.FindMatch(Names, -1, "GAM"));
    }

    [TestMethod]
    public void TheSyntheticParentRow_IsNeverMatched()
    {
        Assert.AreEqual(-1, TypeAheadSearch.FindMatch(Names, -1, "."));
    }

    [TestMethod]
    public void NoMatch_ReturnsMinusOne()
    {
        Assert.AreEqual(-1, TypeAheadSearch.FindMatch(Names, 1, "zzz"));
        Assert.AreEqual(-1, TypeAheadSearch.FindMatch([], -1, "a"));
    }

    [TestMethod]
    public void ASingleCharacter_StartsFromTheNextItemAndWrapsAround()
    {
        // 当前在 fxyb:单敲 f 去下一个 f 开头的;在最后一个 f 上再敲,绕回第一个。
        Assert.AreEqual(4, TypeAheadSearch.FindMatch(Names, 3, "f"));
        Assert.AreEqual(2, TypeAheadSearch.FindMatch(Names, 4, "f"));
    }

    [TestMethod]
    public void ASingleCharacter_StaysOnTheOnlyMatch()
    {
        Assert.AreEqual(5, TypeAheadSearch.FindMatch(Names, 5, "g"));
    }

    [TestMethod]
    public void QuicklyRepeatingOneLetter_CyclesThroughTheItemsStartingWithIt()
    {
        var search = new TypeAheadSearch(new ManualClock());
        int current = -1;
        string term = string.Empty;
        List<int> path = [];
        for (int i = 0; i < 4; i++)
        {
            term = search.Append("f")!;
            current = TypeAheadSearch.FindMatch(Names, current, term);
            path.Add(current);
        }

        Assert.AreEqual("ffff", term, "连按的字符要攒在一起 —— 否则它和慢慢按没有区别。");
        Assert.AreSequenceEqual([2, 3, 4, 2], path);
    }

    [TestMethod]
    public void RepeatedLetters_PreferAnItemThatReallyStartsWithThem()
    {
        string?[] names = ["fa", "ff.txt", "fz"];

        Assert.AreEqual(1, TypeAheadSearch.FindMatch(names, 0, "ff"));
    }

    [TestMethod]
    public void APauseLongerThanTheTimeout_StartsANewTerm()
    {
        var clock = new ManualClock();
        var search = new TypeAheadSearch(clock);

        Assert.AreEqual("f", search.Append("f"));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.AreEqual("fx", search.Append("x"));
        clock.Advance(TypeAheadSearch.Timeout + TimeSpan.FromMilliseconds(1));
        Assert.AreEqual("g", search.Append("g"));
    }

    [TestMethod]
    public void ALeadingSpace_IsNotALocateKeystroke_ButASpaceInsideANameIs()
    {
        var search = new TypeAheadSearch(new ManualClock());

        Assert.IsNull(search.Append(" "), "空格起头不是在找名字,事件要留给列表自己。");
        Assert.AreEqual("my", search.Append("my"));
        Assert.AreEqual("my ", search.Append(" "));
        Assert.AreEqual(6, TypeAheadSearch.FindMatch(Names, -1, search.Append("f")!));
    }

    [TestMethod]
    public void ControlCharacters_AreDropped()
    {
        var search = new TypeAheadSearch(new ManualClock());

        Assert.IsNull(search.Append("\r"));
        Assert.IsNull(search.Append("\b"));
        Assert.AreEqual("a", search.Append("a\t"));
    }

    [TestMethod]
    public void ImeCommittedText_IsAppendedAsAWhole()
    {
        var search = new TypeAheadSearch(new ManualClock());
        string?[] names = ["readme.md", "报告-2026.docx", "报表.xlsx"];

        Assert.AreEqual(1, TypeAheadSearch.FindMatch(names, -1, search.Append("报告")!));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
