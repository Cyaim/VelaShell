using System.Diagnostics;
using VelaShell.XServer.Drawing;

namespace VelaShell.XServer.Tests.Drawing;

/// <summary>分带区域:并 / 交 / 差对着逐像素的真值比,结构始终是合法的分带形式。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class RegionTests
{
    private const int Size = 40;

    /// <summary>逐像素的真值。</summary>
    private static bool[,] Paint(IEnumerable<XRect> rects)
    {
        bool[,] grid = new bool[Size, Size];
        foreach (XRect r in rects)
        {
            for (int y = Math.Max(0, r.Y); y < Math.Min(Size, r.Bottom); y++)
            {
                for (int x = Math.Max(0, r.X); x < Math.Min(Size, r.Right); x++)
                {
                    grid[y, x] = true;
                }
            }
        }
        return grid;
    }

    private static List<XRect> RandomRects(Random random, int count) =>
        [.. Enumerable.Range(0, count).Select(_ => new XRect(random.Next(-4, Size), random.Next(-4, Size), random.Next(0, 16), random.Next(0, 16)))];

    /// <summary>分带形式:带按 y 升序不重叠;同一带 Y、高度相同,按 x 升序、不重叠也不相接;上下相接的两带各段不完全相同。</summary>
    private static void AssertBanded(Region region)
    {
        List<List<XRect>> bands = [];
        foreach (XRect r in region.Rects)
        {
            Assert.IsFalse(r.IsEmpty, "没有空矩形");
            if (bands.Count != 0 && bands[^1][0].Y == r.Y)
            {
                XRect previous = bands[^1][^1];
                Assert.AreEqual(previous.Height, r.Height, "同一带高度相同");
                Assert.IsGreaterThan(previous.Right, r.X, "同一带按 x 升序、不重叠、不相接");
                bands[^1].Add(r);
            }
            else
            {
                if (bands.Count != 0)
                {
                    Assert.IsGreaterThanOrEqualTo(bands[^1][0].Bottom, r.Y, "带按 y 升序、不重叠");
                }
                bands.Add([r]);
            }
        }
        for (int i = 1; i < bands.Count; i++)
        {
            bool touching = bands[i - 1][0].Bottom == bands[i][0].Y;
            bool sameSpans = bands[i - 1].Select(r => (r.X, r.Right)).SequenceEqual(bands[i].Select(r => (r.X, r.Right)));
            Assert.IsFalse(touching && sameSpans, "上下相接且各段相同的两带应当合并");
        }
    }
    private static void AssertSame(bool[,] expected, Region actual, string what)
    {
        AssertBanded(actual);
        bool[,] painted = Paint(actual.Rects);
        int area = actual.Rects.Sum(r => r.Width * r.Height);
        int inside = 0;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                Assert.AreEqual(expected[y, x], painted[y, x], $"{what}:({x}, {y})");
                Assert.AreEqual(expected[y, x], actual.Contains(x, y), $"{what}:Contains({x}, {y})");
                inside += painted[y, x] ? 1 : 0;
            }
        }
        Assert.AreEqual(inside, area, $"{what}:矩形互不重叠(面积之和等于覆盖的像素数)");
    }

    [TestMethod]
    public void 随机的并交差与逐像素真值一致_结构是合法的分带形式()
    {
        Random random = new(20260926);
        for (int round = 0; round < 300; round++)
        {
            // 全部落在 [-4, Size) 里的坐标才可比;裁到 [0, Size) 再比。
            XRect frame = new(0, 0, Size, Size);
            List<XRect> left = RandomRects(random, random.Next(0, 12)), right = RandomRects(random, random.Next(0, 12));
            Region a = Region.FromRects(left).Intersect(frame), b = Region.FromRects(right).Intersect(frame);
            bool[,] pa = Paint(left), pb = Paint(right);
            AssertSame(pa, a, "FromRects");

            bool[,] union = new bool[Size, Size], intersect = new bool[Size, Size], subtract = new bool[Size, Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    union[y, x] = pa[y, x] || pb[y, x];
                    intersect[y, x] = pa[y, x] && pb[y, x];
                    subtract[y, x] = pa[y, x] && !pb[y, x];
                }
            }
            AssertSame(union, a.Clone().Union(b), "Union");
            AssertSame(intersect, a.Clone().Intersect(b), "Intersect");
            AssertSame(subtract, a.Clone().Subtract(b), "Subtract");

            // 逐个矩形并 / 减,与一次性建的一致。
            Region incremental = new();
            foreach (XRect r in left)
            {
                incremental.Union(r.Intersect(frame));
            }
            AssertSame(pa, incremental, "逐个 Union(XRect)");
            Region cut = a.Clone();
            foreach (XRect r in right)
            {
                cut.Subtract(r);
            }
            AssertSame(subtract, cut, "逐个 Subtract(XRect)");
        }
    }

    [TestMethod]
    public void 一万六千块的棋盘格并一个矩形与一次性建区域都是近线性的()
    {
        // 旧的矩形表实现:每并一块都要与已有的每一块互减,一万多块就是亿级的运算。
        List<XRect> board = [];
        for (int y = 0; y < 160; y++)
        {
            for (int x = y % 2; x < 200; x += 2)
            {
                board.Add(new XRect(x, y, 1, 1));
            }
        }
        var watch = Stopwatch.StartNew();
        var region = Region.FromRects(board.OrderBy(r => (r.X * 7919) % 200).ThenBy(r => r.Y));   // 打乱顺序
        region.Union(new XRect(50, 50, 100, 100));
        region.Subtract(new XRect(0, 0, 10, 10));
        watch.Stop();
        Assert.IsFalse(region.Saturated, "一万六千块在上限之内");
        Assert.AreEqual(16000 - (100 * 100 / 2) - (10 * 10 / 2) + (100 * 100), region.Rects.Sum(r => r.Width * r.Height));
        Assert.IsLessThan(2000, watch.ElapsedMilliseconds, $"用了 {watch.ElapsedMilliseconds} 毫秒");
    }

    [TestMethod]
    public void 块数超上限退化成外接矩形并置上Saturated_参与运算的结果也带着它()
    {
        List<XRect> dots = [.. Enumerable.Range(0, Region.MaxRects + 1).Select(i => new XRect(i * 2, 0, 1, 1))];
        var tooMany = Region.FromRects(dots);
        Assert.IsTrue(tooMany.Saturated);
        Assert.AreSequenceEqual([new XRect(0, 0, (Region.MaxRects * 2) + 1, 1)], [.. tooMany.Rects]);

        // 横条减竖条:结果是一张网格,块数是两边的乘积。
        var bars = Region.FromRects(Enumerable.Range(0, 200).Select(i => new XRect(0, i * 2, 400, 1)));
        var columns = Region.FromRects(Enumerable.Range(0, 200).Select(i => new XRect(i * 2, 0, 1, 400)));
        Assert.IsFalse(bars.Saturated || columns.Saturated);
        Region grid = bars.Clone().Subtract(columns);
        Assert.IsTrue(grid.Saturated, "200 × 200 块超了上限");
        Assert.AreEqual(bars.Bounds, grid.Bounds, "差退化成被减数的外接矩形:盖住真实结果");
        Assert.IsTrue(grid.Clone().Union(new XRect(0, 0, 1, 1)).Saturated, "由它算出来的也带着标志");
    }

    [TestMethod]
    public void 一带很多段被很多带切开时按预算止损_不会退化成平方()
    {
        // 一带里一万六千段;减数是一万六千条细带,与它们都不相交 —— 每条细带都要把那一带整个过一遍。
        const int n = 16000;
        var comb = Region.FromRects(Enumerable.Range(0, n).Select(i => new XRect(i * 2, 0, 1, n * 2)));
        var thin = Region.FromRects(Enumerable.Range(0, n).Select(i => new XRect((n * 2) + 10, i * 2, 1, 1)));
        var watch = Stopwatch.StartNew();
        Region result = comb.Clone().Subtract(thin);
        watch.Stop();
        Assert.IsTrue(result.Saturated, "超了归并预算");
        Assert.IsLessThan(1000, watch.ElapsedMilliseconds, $"用了 {watch.ElapsedMilliseconds} 毫秒");
    }
}
