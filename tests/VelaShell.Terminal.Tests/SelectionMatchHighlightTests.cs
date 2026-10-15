using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("SelectionMatchHighlight")]
public sealed class SelectionMatchHighlightTests
{
    [TestMethod]
    public void Drag_HighlightsCaseInsensitiveSubstrings_WithoutChangingCopy() =>
        Run("error terror ERROR\r\nerror", (window, control) =>
        {
            Drag(window, control, (0, 0), (0, 5));
            Assert.HasCount(4, control.SelectionMatchesForTest);
            Assert.AreEqual("error", control.GetSelectedText());
            Assert.AreEqual(7, control.SelectionMatchesForTest[1].StartCol);
        });

    [TestMethod]
    public void DoubleClick_HighlightsAllWords() =>
        Run("alpha alpha\r\nALPHA", (window, control) =>
        {
            Point point = Cell(control, 0, 2);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Paint(window);
            Assert.AreEqual("alpha", control.GetSelectedText());
            Assert.HasCount(3, control.SelectionMatchesForTest);
        });

    [TestMethod]
    public void PhraseAndTrailingSpace_AreMatchedExactly_NotTrimmedForCopy() =>
        Run("foo bar foo bar\r\nfooX foo foo", (window, control) =>
        {
            Drag(window, control, (0, 0), (0, 7));
            Assert.HasCount(2, control.SelectionMatchesForTest);
            Drag(window, control, (1, 5), (1, 9));
            Assert.AreEqual("foo", control.GetSelectedText());
            Assert.IsTrue(control.SelectionMatchesForTest.All(hit => hit.Length == 4));
            Assert.IsFalse(control.SelectionMatchesForTest.Any(hit => hit.Row == 1 && hit.StartCol == 0));
        });

    [TestMethod]
    public void ExplicitTrailingSpaces_RemainPartOfTheQuery() =>
        Run("foo \r\nfoo \r\nfoo", (window, control) =>
        {
            Drag(window, control, (0, 0), (0, 4));
            CollectionAssert.AreEqual(new[] { 0, 1 }, control.SelectionMatchesForTest.Select(hit => hit.Row).ToArray());
        });

    [TestMethod]
    public void UnicodeSelection_IncludesCombiningMarksAndWideColumns() =>
        Run("中e\u0301 中e\u0301", (window, control) =>
        {
            Drag(window, control, (0, 0), (0, 3));
            CollectionAssert.AreEqual(
                new[] { new BufferSearchHit(0, 0, 3), new BufferSearchHit(0, 4, 3) },
                control.SelectionMatchesForTest.ToArray());
        });

    [TestMethod]
    public void NewSelectionAndClick_ReplaceThenClearMatches() =>
        Run("alpha alpha beta beta", (window, control) =>
        {
            Drag(window, control, (0, 0), (0, 5));
            Drag(window, control, (0, 12), (0, 16));
            Assert.HasCount(2, control.SelectionMatchesForTest);
            Assert.IsTrue(control.SelectionMatchesForTest.All(hit => hit.Length == 4));
            Point point = Cell(control, 1, 1);
            window.MouseDown(point, MouseButton.Left);
            Assert.IsEmpty(control.SelectionMatchesForTest);
            window.MouseUp(point, MouseButton.Left);
            Paint(window);
            Assert.IsEmpty(control.SelectionMatchesForTest);
        });

    [TestMethod]
    public void ComplexAndWhitespaceSelections_DoNotActivateMatches() =>
        Run("alpha alpha\r\nalpha alpha", (window, control) =>
        {
            Drag(window, control, (0, 1), (0, 5), RawInputModifiers.Alt);
            Assert.IsEmpty(control.SelectionMatchesForTest);
            Drag(window, control, (0, 0), (1, 5));
            Assert.IsEmpty(control.SelectionMatchesForTest);
            Drag(window, control, (1, 0), (1, 5));
            Drag(window, control, (0, 0), (0, 5), RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.IsEmpty(control.SelectionMatchesForTest);
            Assert.AreEqual("alpha\nalpha", control.GetSelectedText());
            Drag(window, control, (2, 0), (2, 4));
            Assert.IsEmpty(control.SelectionMatchesForTest);
        });

    [TestMethod]
    public void ShiftExtension_ReplacesMatchQuery() =>
        Run("foo bar foo bar", (window, control) =>
        {
            Drag(window, control, (0, 0), (0, 3));
            Point point = Cell(control, 0, 7);
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.Shift);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.Shift);
            Paint(window);
            Assert.AreEqual("foo bar", control.GetSelectedText());
            Assert.HasCount(2, control.SelectionMatchesForTest);
            Assert.AreEqual(7, control.SelectionMatchesForTest[0].Length);
        });

    [TestMethod]
    public void OutputUpdates_AreCoalescedUntilRender_AndIdleRenderDoesNotSearch() =>
        Run("alpha alpha", (window, control) =>
        {
            Drag(window, control, (0, 0), (0, 5));
            int count = control.SelectionMatchRefreshCountForTest;
            control.Feed(Encoding.UTF8.GetBytes("\r\nalpha"));
            control.Feed(Encoding.UTF8.GetBytes(" alpha"));
            Assert.AreEqual(count, control.SelectionMatchRefreshCountForTest);
            Paint(window);
            Assert.HasCount(4, control.SelectionMatchesForTest);
            Assert.AreEqual(count + 1, control.SelectionMatchRefreshCountForTest);
            control.InvalidateTerminal();
            Paint(window);
            Assert.AreEqual(count + 1, control.SelectionMatchRefreshCountForTest);
        });

    [TestMethod]
    public void Scrolling_SearchesOnlyVisibleRows() =>
        Run("", (window, control) =>
        {
            int rows = control.Rows;
            control.Feed(Encoding.UTF8.GetBytes(string.Join("\r\n", Enumerable.Repeat("alpha", rows * 3))));
            Paint(window);
            Drag(window, control, (0, 0), (0, 5));
            Assert.HasCount(rows, control.SelectionMatchesForTest);
            Assert.IsTrue(control.SelectionMatchesForTest.All(hit => hit.Row >= control.ViewportRow));
            control.ScrollOffset = control.MaxScrollOffset;
            Paint(window);
            Assert.HasCount(rows, control.SelectionMatchesForTest);
            Assert.AreEqual(0, control.SelectionMatchesForTest[0].Row);
        });

    [TestMethod]
    public void Folding_ExcludesHiddenRowsAndUpdatesWhenExpanded() =>
        Run("alpha\r\nalpha\r\nalpha\r\nalpha", (window, control) =>
        {
            Drag(window, control, (0, 0), (0, 5));
            var point = new Point(control.GutterForTest.FoldLeft + 2, 3 * control.CellHeightForTest + 2);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Paint(window);
            Assert.AreEqual(1, control.FoldCountForTest);
            Assert.IsLessThan(4, control.SelectionMatchesForTest.Count);
            point = new(control.GutterForTest.FoldLeft + 2, 2);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Paint(window);
            Assert.HasCount(4, control.SelectionMatchesForTest);
        });

    [TestMethod]
    public void SearchHighlights_AreIndependent_AndNavigationDoesNotStartSelectionMatching() =>
        Run("alpha alpha beta", (window, control) =>
        {
            control.SetSearchHighlights([new(0, 12, 4)], 0);
            Drag(window, control, (0, 0), (0, 5));
            control.ClearSearchHighlights();
            Paint(window);
            Assert.HasCount(2, control.SelectionMatchesForTest);
            control.ShowHit(new(0, 12, 4));
            Paint(window);
            Assert.AreEqual("beta", control.GetSelectedText());
            Assert.IsEmpty(control.SelectionMatchesForTest);
        });

    [TestMethod]
    public void CopyOnSelect_AndCtrlC_PreserveOriginalCopySemantics() =>
        Run("alpha alpha", (window, control) =>
        {
            control.CopyOnSelect = true;
            Drag(window, control, (0, 0), (0, 5));
            Assert.AreEqual("alpha", control.GetSelectedText());
            Assert.HasCount(2, control.SelectionMatchesForTest);
            Assert.IsTrue(control.TryCopyOnCtrlC());
            Paint(window);
            Assert.IsEmpty(control.SelectionMatchesForTest);
            Assert.AreEqual("", control.GetSelectedText());
        });

    [TestMethod]
    public void InputResizeAndScreenSwitch_ClearMatches() =>
        Run("alpha alpha", (window, control) =>
        {
            Drag(window, control, (0, 0), (0, 5));
            control.WriteTextInput("x");
            Paint(window);
            Assert.IsEmpty(control.SelectionMatchesForTest);
            Drag(window, control, (0, 0), (0, 5));
            control.Feed(Encoding.ASCII.GetBytes("\e[?1049h"));
            Paint(window);
            Assert.IsEmpty(control.SelectionMatchesForTest);
            control.Feed(Encoding.ASCII.GetBytes("\e[?1049l"));
            Paint(window);
            Assert.IsEmpty(control.SelectionMatchesForTest);
            Drag(window, control, (0, 0), (0, 5));
            control.Resize(control.Columns + 1, control.Rows);
            Paint(window);
            Assert.IsEmpty(control.SelectionMatchesForTest);
        });

    [TestMethod]
    public void LegacyWriteInput_ClearsSelectionMatches() =>
        Run("alpha alpha", (window, control) =>
        {
            Drag(window, control, (0, 0), (0, 5));
            control.WriteInput("x"u8.ToArray());
            Paint(window);
            Assert.IsEmpty(control.SelectionMatchesForTest);
            Assert.AreEqual("", control.GetSelectedText());
        });

    [TestMethod]
    public void BatchedScreenRoundTrip_ClearsMatchesEvenWhenFinalScreenIsTheSame() =>
        Run("alpha alpha", (window, control) =>
        {
            Drag(window, control, (0, 0), (0, 5));
            control.Feed("\e[?1049h\e[?1049l"u8.ToArray());
            Paint(window);
            Assert.IsEmpty(control.SelectionMatchesForTest);
        });

    [TestMethod]
    public void ImeCompositionFromHistory_ClearsMatchesBeforeReturningToLiveScreen() =>
        Run("", (window, control) =>
        {
            control.Feed(Encoding.UTF8.GetBytes(string.Join("\r\n", Enumerable.Repeat("alpha", control.Rows * 2))));
            control.ScrollOffset = control.MaxScrollOffset;
            Paint(window);
            Drag(window, control, (0, 0), (0, 5));
            Assert.IsNotEmpty(control.SelectionMatchesForTest);
            control.ImeClientForTest!.SetPreeditText("ni", 2);
            Paint(window);
            Assert.AreEqual(0, control.ScrollOffset);
            Assert.IsEmpty(control.SelectionMatchesForTest);
        });

    [TestMethod]
    public void WheelAtScrollBoundary_DoesNotRescanUnchangedViewport() =>
        Run("alpha alpha", (window, control) =>
        {
            Drag(window, control, (0, 0), (0, 5));
            int refreshes = control.SelectionMatchRefreshCountForTest;
            window.MouseWheel(Cell(control, 0, 2), new(0, -1));
            Paint(window);
            Assert.AreEqual(refreshes, control.SelectionMatchRefreshCountForTest);
        });

    private static void Run(string text, Action<Window, VelaTerminalControl> body) =>
        HeadlessTestSession.Current.Dispatch(() =>
        {
            using var control = new VelaTerminalControl
            {
                CopyOnSelect = false,
                CursorBlink = false,
                ShowFoldMarker = true,
                CtrlCCopiesWhenSelected = true,
            };
            var window = new Window { Width = 640, Height = 360, Content = control };
            window.Show();
            try
            {
                Paint(window);
                control.Feed(Encoding.UTF8.GetBytes(text));
                Paint(window);
                body(window, control);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static void Drag(Window window, VelaTerminalControl control,
        (int Row, int Col) from, (int Row, int Col) to,
        RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseDown(Cell(control, from.Row, from.Col), MouseButton.Left, modifiers);
        window.MouseMove(Cell(control, to.Row, to.Col), modifiers);
        window.MouseUp(Cell(control, to.Row, to.Col), MouseButton.Left, modifiers);
        Paint(window);
    }

    private static Point Cell(VelaTerminalControl control, int row, int col) =>
        new(control.GutterForTest.TotalWidth + col * control.CellWidthForTest + 1,
            row * control.CellHeightForTest + 1);

    private static void Paint(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
    }
}
