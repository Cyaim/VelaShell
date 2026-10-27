using Avalonia.Input;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 图标视图的格子几何(<see cref="TileGridMath" />):每行几格、视口里该实现哪些项、框选矩形碰到哪些格子、
/// 方向键的去向。这些都只由下标算出、不问容器 —— 面板是虚拟化的,视口外的格子没有容器。
/// </summary>
[TestClass]
[TestCategory("Marquee")]
public class TileGridMathTests
{
    private const double W = 100, H = 96;

    [TestMethod]
    [DataRow(850d, 8)]
    [DataRow(800d, 8)]
    [DataRow(799.5d, 7)]
    [DataRow(40d, 1)]
    [DataRow(0d, 1)]
    [DataRow(double.PositiveInfinity, 1)]
    public void Columns_FitsWholeTilesAndNeverDropsBelowOne(double width, int expected) =>
        Assert.AreEqual(expected, TileGridMath.Columns(width, W));

    [TestMethod]
    public void Rows_RoundsUpAPartialLastRow()
    {
        Assert.AreEqual(0, TileGridMath.Rows(0, 8));
        Assert.AreEqual(1, TileGridMath.Rows(8, 8));
        Assert.AreEqual(2, TileGridMath.Rows(9, 8));
    }

    [TestMethod]
    public void IndexRange_CoversEveryRowTheBandTouches()
    {
        // 第 1 行中间到第 2 行中间:两整行,共 8 格。
        Assert.AreEqual((4, 11), TileGridMath.IndexRange(H * 1.5, H * 2.5, H, columns: 4, count: 20));
        // 正好落在行边界上的下沿不算碰到下一行。
        Assert.AreEqual((0, 3), TileGridMath.IndexRange(0, H, H, columns: 4, count: 20));
    }

    [TestMethod]
    public void IndexRange_StopsAtTheLastItem_AndIsEmptyOutsideTheContent()
    {
        Assert.AreEqual((16, 17), TileGridMath.IndexRange(H * 4, H * 10, H, columns: 4, count: 18));
        Assert.AreEqual((-1, -1), TileGridMath.IndexRange(H * 6, H * 7, H, columns: 4, count: 18));
        Assert.AreEqual((-1, -1), TileGridMath.IndexRange(-50, -1, H, columns: 4, count: 18));
        Assert.AreEqual((-1, -1), TileGridMath.IndexRange(0, 500, H, columns: 4, count: 0));
    }

    [TestMethod]
    public void IndicesInRect_SelectsTheTwoDimensionalBlock_InEitherDragDirection()
    {
        // 4 列:从 (行0,列1) 拖到 (行1,列2) —— 选中 1、2、5、6,而不是整行。
        int[] expected = [1, 2, 5, 6];
        Assert.AreSequenceEqual(expected, [.. TileGridMath.IndicesInRect(150, 40, 250, 140, W, H, 4, 20)]);
        Assert.AreSequenceEqual(expected, [.. TileGridMath.IndicesInRect(250, 140, 150, 40, W, H, 4, 20)]);
    }

    [TestMethod]
    public void IndicesInRect_SkipsTheEmptyCellsOfAPartialLastRow() =>
        // 4 列 6 项:第二行只有 4、5 两格,矩形盖到右下角的空位也不该凭空多出 6、7。
        Assert.AreSequenceEqual([0, 1, 2, 3, 4, 5], [.. TileGridMath.IndicesInRect(10, 50, 390, 150, W, H, 4, 6)]);

    [TestMethod]
    public void IndicesInRect_IsEmptyRightOfTheLastColumnAndBelowTheLastRow()
    {
        // 窗口比 4 列宽:最后一列右边是空白,在那里拖不该选中任何项。
        Assert.IsEmpty(TileGridMath.IndicesInRect(410, 10, 600, 300, W, H, 4, 20));
        Assert.IsEmpty(TileGridMath.IndicesInRect(10, H * 5 + 1, 300, H * 6, W, H, 4, 20));
    }

    [TestMethod]
    public void IndicesInRect_WithOneColumn_BehavesLikeTheRowBand() =>
        // 列表视图是一格占满整行:矩形只要横向碰到这一列,结果就只由上下两边决定。
        Assert.AreSequenceEqual([1, 2, 3], [.. TileGridMath.IndicesInRect(20, 30, 900, 100, 880, 28, 1, 7)]);

    [TestMethod]
    public void Navigate_LeftRightStepThroughTheRowAndContinueOnTheNextOne()
    {
        Assert.AreEqual(4, TileGridMath.Navigate(3, NavigationDirection.Right, 4, 10, 3, wrap: false));
        Assert.AreEqual(3, TileGridMath.Navigate(4, NavigationDirection.Left, 4, 10, 3, wrap: false));
        Assert.AreEqual(0, TileGridMath.Navigate(0, NavigationDirection.Left, 4, 10, 3, wrap: false), "走到头原地不动。");
        Assert.AreEqual(9, TileGridMath.Navigate(0, NavigationDirection.Left, 4, 10, 3, wrap: true));
    }

    [TestMethod]
    public void Navigate_UpDownKeepTheColumn()
    {
        Assert.AreEqual(6, TileGridMath.Navigate(2, NavigationDirection.Down, 4, 10, 3, wrap: false));
        Assert.AreEqual(2, TileGridMath.Navigate(6, NavigationDirection.Up, 4, 10, 3, wrap: false));
        Assert.AreEqual(2, TileGridMath.Navigate(2, NavigationDirection.Up, 4, 10, 3, wrap: false), "第一行再往上原地不动。");
    }

    [TestMethod]
    public void Navigate_DownIntoAShorterLastRow_LandsOnTheLastItem()
    {
        // 4 列 10 项:第三行只有 8、9。从 7 往下,正下方没有格子,落到最后一项(资源管理器同款)。
        Assert.AreEqual(9, TileGridMath.Navigate(7, NavigationDirection.Down, 4, 10, 3, wrap: false));
        Assert.AreEqual(9, TileGridMath.Navigate(9, NavigationDirection.Down, 4, 10, 3, wrap: false), "最后一行再往下原地不动。");
    }

    [TestMethod]
    public void Navigate_PageKeysMoveAScreenfulOfRows()
    {
        Assert.AreEqual(13, TileGridMath.Navigate(1, NavigationDirection.PageDown, 4, 40, 3, wrap: false));
        Assert.AreEqual(1, TileGridMath.Navigate(13, NavigationDirection.PageUp, 4, 40, 3, wrap: false));
        Assert.AreEqual(37, TileGridMath.Navigate(33, NavigationDirection.PageDown, 4, 40, 3, wrap: false), "不足一页时走到同一列的最后一行。");
        Assert.AreEqual(3, TileGridMath.Navigate(7, NavigationDirection.PageUp, 4, 40, 3, wrap: false));
    }

    [TestMethod]
    public void Navigate_HomeEndAndUnknownDirections()
    {
        Assert.AreEqual(0, TileGridMath.Navigate(5, NavigationDirection.First, 4, 10, 3, wrap: false));
        Assert.AreEqual(9, TileGridMath.Navigate(5, NavigationDirection.Last, 4, 10, 3, wrap: false));
        Assert.AreEqual(-1, TileGridMath.Navigate(5, (NavigationDirection)999, 4, 10, 3, wrap: false));
        Assert.AreEqual(-1, TileGridMath.Navigate(0, NavigationDirection.Down, 4, 0, 3, wrap: false));
    }
}
