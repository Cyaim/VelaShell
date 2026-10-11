using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 资源管理器里悬停一条连接显示它的备注(#549)。
/// </summary>
/// <remarks>
/// 提示文字由节点自己算(<see cref="SessionTreeNodeViewModel.NotesTip" />):没写备注时必须是 null ——
/// 空串也会弹出一块空的浮层;太长的要截断 —— 提示框只管宽度不管高度。
/// </remarks>
[TestClass]
[TestCategory("SessionTree")]
public class SessionTreeNotesTests
{
    [TestMethod]
    public async Task LoadingTheTree_CarriesEachSessionsNotesOntoItsRow()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        var group = new ServerGroup { Id = Guid.NewGuid(), Name = "Prod" };
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>
        {
            new() { Name = "bastion", Host = "10.0.0.1", GroupId = group.Id, Notes = "跳板机\n值班:运维二组" },
            new() { Name = "plain", Host = "10.0.0.2" },
        }));

        SessionTreeViewModel vm = new(repository);
        await vm.LoadCommand.Execute().FirstAsync();

        SessionTreeNodeViewModel bastion = vm.Rows.Single(row => row.Name == "bastion");
        SessionTreeNodeViewModel plain = vm.Rows.Single(row => row.Name == "plain");
        Assert.AreEqual("跳板机\n值班:运维二组", bastion.NotesTip, "分组里的会话也要带上备注");
        Assert.IsNull(plain.NotesTip, "没写备注的连接不弹提示");
        Assert.IsNull(vm.Rows.Single(row => row.IsGroup).NotesTip, "分组行没有备注");
    }

    [TestMethod]
    public void AddingASession_CarriesItsNotes()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        SessionTreeViewModel vm = new(repository);

        vm.AddSession(new() { Name = "fresh", Host = "h", Notes = "新机器" });

        Assert.AreEqual("新机器", vm.Rows.Single().NotesTip);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  \r\n\t ")]
    public void NoNotes_MeansNoTooltip(string? notes) =>
        Assert.IsNull(Node(notes).NotesTip);

    [TestMethod]
    public void SurroundingBlankLines_AreNotShown() =>
        Assert.AreEqual("第一行\n\n第三行", Node("\n\n  第一行\n\n第三行  \n\n").NotesTip);

    [TestMethod]
    public void ManyLines_AreCutAndMarked()
    {
        string notes = string.Join('\n', Enumerable.Range(1, SessionTreeNodeViewModel.NotesTipMaxLines + 5).Select(i => $"第 {i} 行"));

        string tip = Node(notes).NotesTip!;

        string[] lines = tip.Split('\n');
        Assert.HasCount(SessionTreeNodeViewModel.NotesTipMaxLines, lines);
        Assert.AreEqual($"第 {SessionTreeNodeViewModel.NotesTipMaxLines} 行…", lines[^1], "截断了要看得出来");
    }

    [TestMethod]
    public void ExactlyTheLineLimit_IsNotMarkedAsCut()
    {
        string notes = string.Join('\n', Enumerable.Range(1, SessionTreeNodeViewModel.NotesTipMaxLines).Select(i => $"第 {i} 行"));

        Assert.AreEqual(notes, Node(notes).NotesTip);
    }

    [TestMethod]
    public void OneVeryLongLine_IsCutAndMarked()
    {
        string tip = Node(new string('x', SessionTreeNodeViewModel.NotesTipMaxLength * 3)).NotesTip!;

        Assert.AreEqual(new string('x', SessionTreeNodeViewModel.NotesTipMaxLength) + "…", tip);
    }

    [TestMethod]
    public void CuttingAtTheLengthLimit_DoesNotSplitASurrogatePair()
    {
        // 让一个 emoji(两个 UTF-16 码元)正好跨在截断点上。
        string notes = new string('x', SessionTreeNodeViewModel.NotesTipMaxLength - 1) + "😀" + "tail";

        string tip = Node(notes).NotesTip!;

        Assert.IsFalse(char.IsHighSurrogate(tip[^2]), "截断点落在 emoji 中间,提示里会画出半个字符");
        Assert.AreEqual(new string('x', SessionTreeNodeViewModel.NotesTipMaxLength - 1) + "…", tip);
    }

    [TestMethod]
    public void ChangingTheNotes_RefreshesTheTooltip()
    {
        SessionTreeNodeViewModel node = Node("旧备注");
        List<string?> raised = [];
        node.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        node.Notes = "新备注";

        Assert.Contains(nameof(SessionTreeNodeViewModel.NotesTip), raised);
        Assert.AreEqual("新备注", node.NotesTip);
    }

    private static SessionTreeNodeViewModel Node(string? notes) =>
        new(Guid.NewGuid(), "s", false) { Notes = notes };
}
